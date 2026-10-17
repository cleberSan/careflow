#!/usr/bin/env bash
# Ensaio da restauração de verdade no CI (workflow "Stack de produção", ADR-0013). Nunca roda na VM.
#   restore-rehearsal.sh seed     cadastra um paciente sintético numa clínica que não é do Painel (com chave no cofre)
#   restore-rehearsal.sh erase    pede a eliminação dele, confirma o código da caixa simulada e espera concluir
# Entre os dois o workflow faz o backup; depois do erase, o careflow-restore traz a chave de volta com o backup e
# precisa reaplicar a eliminação (o registro do Rights no ar é mais novo que o backup). Estado em $REHEARSAL_STATE.
set -euo pipefail
# shellcheck source=deploy/prod/bin/careflow-common.sh
source /usr/local/sbin/careflow-common.sh
STATE="${REHEARSAL_STATE:?defina REHEARSAL_STATE}"

# Chamada JSON a um serviço pela rede interna; o token vai pelo stdin, o corpo (sintético) por argumento.
api() {
  local method="$1" url="$2" token="$3" body="${4:-}"
  local data=()
  [[ -z "$body" ]] || data=(-H 'Content-Type: application/json' --data "$body")
  printf 'Authorization: Bearer %s\n' "$token" \
    | docker run --rm -i --network "$BACKEND_NETWORK" --read-only --cap-drop ALL "$CURL_IMAGE" \
        -fsS -X "$method" -H @- "${data[@]}" --max-time 20 "$url"
}

json() { python3 -I -c "import json,sys; print(json.load(sys.stdin)$1)"; }

eventually() {
  local description="$1"; shift
  for _ in $(seq 1 60); do
    if output="$("$@" 2>/dev/null)" && [[ -n "$output" ]]; then printf '%s' "$output"; return 0; fi
    sleep 2
  done
  die "tempo esgotado esperando: $description"
}

case "${1:-}" in
  seed)
    clinic="$(python3 -I -c 'import uuid; print(uuid.uuid4())')"
    cpf="$(python3 -I - <<'PY'
import random
digits = [random.randint(0, 9) for _ in range(9)]
for size in (9, 10):
    total = sum(d * w for d, w in zip(digits, range(size + 1, 1, -1)))
    digits.append(0 if total % 11 < 2 else 11 - total % 11)
print("".join(map(str, digits)))
PY
)"
    body="$(printf '{"fullName":"Paciente Ensaio %s","cpf":"%s","birthDate":"1990-05-17","email":"ensaio.%s@example.com","phone":"5511987654321"}' \
      "${clinic:0:8}" "$cpf" "${clinic:0:8}")"
    patient="$(api POST http://patient-vault:8080/v1/patients "$(mint_token "$clinic" careflow-patient-vault vault.write)" "$body" | json "['patientRef']")"
    api GET "http://patient-vault:8080/v1/patients/$patient" "$(mint_token "$clinic" careflow-patient-vault vault.read)" > /dev/null
    printf 'clinic=%s\npatient=%s\n' "$clinic" "$patient" > "$STATE"
    log "paciente sintético cadastrado (clínica fora do Painel)"
    ;;
  erase)
    # shellcheck disable=SC1090
    source "$STATE"
    request="$(api POST http://rights:8080/v1/subject-requests "$(mint_token "$clinic" careflow-rights rights.write)" \
      "{\"patientRef\":\"$patient\",\"kind\":\"erasure\"}" | json "['id']")"
    inbox_token="$(mint_token "$clinic" careflow-notifications notifications.demo-inbox)"
    read_code() {
      api GET "http://notifications:8080/v1/demo/verification-messages/$request" "$inbox_token" \
        | python3 -I -c 'import json,re,sys; m=json.load(sys.stdin); print(re.search(r"\b(\d{6})\b", m[-1]["text"]).group(1) if m else "")'
    }
    code="$(eventually "código na caixa simulada" read_code)"
    api POST "http://rights:8080/v1/subject-requests/$request/identity-confirmation" "$(mint_token "$clinic" careflow-rights rights.write)" \
      "{\"code\":\"$code\"}" > /dev/null
    read_status() {
      local status
      status="$(api GET "http://rights:8080/v1/subject-requests/$request" "$(mint_token "$clinic" careflow-rights rights.read)" | json "['status']")"
      [[ "$status" == completed || "$status" == denied ]] && printf '%s' "$status"
    }
    status="$(eventually "eliminação concluída" read_status)"
    [[ "$status" == completed ]] || die "a eliminação terminou como $status"
    keys="$(psql_owner careflow_vault -c "SELECT count(*) FROM patient_keys WHERE patient_ref = '$patient';")"
    [[ "$keys" == 0 ]] || die "a chave do paciente continua no cofre depois da eliminação"
    log "eliminação concluída; a chave do paciente sumiu do cofre"
    ;;
  *)
    sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
