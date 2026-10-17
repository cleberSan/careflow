# Funções comuns dos scripts de backup e restauração da demonstração hospedada (ADR-0013). Carregado com source.
# shellcheck shell=bash disable=SC2034

ETC="${CAREFLOW_ETC:-/etc/careflow}"
POSTGRES_CONTAINER="${CAREFLOW_POSTGRES_CONTAINER:-careflow-postgres-1}"
BACKEND_NETWORK="${CAREFLOW_BACKEND_NETWORK:-careflow_backend}"
CURL_IMAGE="curlimages/curl:8.16.0"
SCRATCH_POSTGRES_IMAGE="postgres:17.6-alpine"
DATABASES=(careflow_scheduling careflow_vault careflow_consent careflow_audit careflow_notifications careflow_triage careflow_rights)
SERVICE_PREFIXES=(scheduling vault consent audit notifications triage rights)

log() { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }
die() { log "ERRO: $*" >&2; exit 1; }

require_root() { [[ "$(id -u)" -eq 0 ]] || die "rode como root"; }

# Bucket e namespace do Object Storage (não são segredo; a VM autentica como instance principal, sem chave).
load_backup_settings() {
  [[ -f "$ETC/backup.env" ]] || die "falta $ETC/backup.env com OCI_NAMESPACE e OCI_BUCKET"
  # shellcheck disable=SC1091
  source "$ETC/backup.env"
  [[ "${OCI_NAMESPACE:-}" =~ ^[a-z0-9]+$ ]] || die "OCI_NAMESPACE inválido em $ETC/backup.env"
  [[ "${OCI_BUCKET:-}" =~ ^[A-Za-z0-9._-]+$ ]] || die "OCI_BUCKET inválido em $ETC/backup.env"
}

oci_os() { oci --auth instance_principal os object "$@" --namespace-name "$OCI_NAMESPACE" --bucket-name "$OCI_BUCKET"; }

# Último backup completo: só conta a pasta que tem o SHA256SUMS, que é o último arquivo enviado.
latest_backup() {
  oci_os list --prefix "careflow/" --all --query "data[?ends_with(name, '/SHA256SUMS')].name" --raw-output \
    | tr -d '[]", ' | sed '/^$/d' | sort | tail -n1 | cut -d/ -f2
}

download_backup() {
  local id="$1" target="$2"
  [[ "$id" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || die "backup inválido: $id (esperado AAAAMMDDTHHMMSSZ)"
  mkdir -p "$target"
  oci_os get --name "careflow/$id/SHA256SUMS" --file "$target/SHA256SUMS" >/dev/null
  while read -r _ file; do
    [[ "$file" =~ ^[a-z_]+\.dump$ ]] || die "nome inesperado no SHA256SUMS: $file"
    oci_os get --name "careflow/$id/$file" --file "$target/$file" >/dev/null
  done < "$target/SHA256SUMS"
  (cd "$target" && sha256sum --quiet -c SHA256SUMS) || die "o backup $id não confere com o SHA256SUMS"
  log "backup $id baixado e conferido"
}

# Token HS256 curto do emissor da demonstração, como os do Demo: uma clínica, uma audiência, um escopo.
# A chave sai de /etc/careflow/env/demo.env e só passa pela memória do processo.
mint_token() {
  local tenant="$1" audience="$2" scope="$3"
  DEMO_SIGNING_KEY="$(sed -n 's/^Demo__Tokens__SigningKey=//p' "$ETC/env/demo.env")" \
  python3 -I - "$tenant" "$audience" "$scope" <<'PY'
import base64, hashlib, hmac, json, os, sys, time, uuid
tenant, audience, scope = sys.argv[1:4]
key = base64.b64decode(os.environ["DEMO_SIGNING_KEY"])
def b64(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()
now = int(time.time())
header = b64(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
payload = b64(json.dumps({
    "iss": "careflow-demo-hosted", "aud": audience, "sub": "careflow-restore", "tenant_id": tenant,
    "scope": scope, "jti": uuid.uuid4().hex, "iat": now, "nbf": now, "exp": now + 300,
}).encode())
signature = b64(hmac.new(key, f"{header}.{payload}".encode(), hashlib.sha256).digest())
print(f"{header}.{payload}.{signature}")
PY
}

# Chamada a um serviço pela rede interna, de um container curl descartável (as imagens dos serviços não têm curl).
# O token vai por arquivo de cabeçalho em stdin, nunca na linha de comando.
call_service() {
  local method="$1" url="$2" token="$3"
  printf 'Authorization: Bearer %s\n' "$token" \
    | docker run --rm -i --network "$BACKEND_NETWORK" --read-only --cap-drop ALL "$CURL_IMAGE" \
        -sS -o /dev/null -w '%{http_code}' -X "$method" -H @- --max-time 20 "$url"
}

# Mesmo container, mas devolve o corpo da resposta (JSON pequeno) em vez do status.
call_service_body() {
  local method="$1" url="$2" token="$3"
  printf 'Authorization: Bearer %s\n' "$token" \
    | docker run --rm -i --network "$BACKEND_NETWORK" --read-only --cap-drop ALL "$CURL_IMAGE" \
        -fsS -X "$method" -H @- --max-time 20 "$url"
}

psql_owner() {
  local database="$1"; shift
  docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -X -q -A -t -U careflow -d "$database" "$@"
}
