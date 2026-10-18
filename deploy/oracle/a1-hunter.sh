#!/usr/bin/env bash
# Tenta criar a VM Ampere A1 gratuita da demonstração hospedada (ADR-0013) com os dados que o provision.sh passa.
#
#   a1-hunter.sh once   uma rodada: cada domínio de disponibilidade e cada tamanho, do maior ao menor. Imprime o
#                       OCID e sai com 0 quando cria; sai com 3 quando não há vaga e com 1 em qualquer outro erro.
#   a1-hunter.sh loop   o robô: roda na VM mínima careflow-cacador e repete a rodada a cada INTERVAL_SECONDS até
#                       criar a A1, achar uma já criada ou passar de DEADLINE_EPOCH. Depois apaga a própria VM.
#
# Nada aqui é segredo: são OCIDs, a chave SSH pública do Cloud Shell e o cloud-init público da A1. No robô, a CLI
# autentica como instance principal (OCI_CLI_AUTH=instance_principal), com permissão só no compartimento careflow.
set -euo pipefail

: "${REGION:?}" "${COMPARTMENT:?}" "${SUBNET:?}" "${IMAGE:?}" "${ADS:?}" "${SIZES:?}" "${BOOT_GB:?}" "${NAME:?}"
: "${SSH_PUBLIC_KEY_FILE:?}" "${USER_DATA_FILE:?}"
SHAPE="VM.Standard.A1.Flex"
NO_CAPACITY=3
read -r -a ads <<< "$ADS"
read -r -a sizes <<< "$SIZES"

o() { oci --region "$REGION" "$@"; }
note() { printf '%s\n' "$*" >&2; }

# OCID da A1 que ainda não foi apagada, ou vazio.
existing_a1() {
  # shellcheck disable=SC2016 # os acentos graves são literais do JMESPath, não expansão do shell
  o compute instance list --compartment-id "$COMPARTMENT" --display-name "$NAME" --all \
    --query 'data[?"lifecycle-state"!=`TERMINATED` && "lifecycle-state"!=`TERMINATING`] | [0].id' --raw-output |
    grep -E '^ocid1\.instance\.' || true
}

# Uma rodada. Deixa o OCID em LAUNCHED e devolve 0; devolve 3 sem vaga; devolve 1 com a mensagem em LAST_ERROR.
LAUNCHED=""
LAST_ERROR=""
launch_round() {
  local ad size ocpus memory_gb out
  for ad in "${ads[@]}"; do
    for size in "${sizes[@]}"; do
      ocpus="${size%%:*}" memory_gb="${size##*:}"
      note "$(date +%H:%M) tentativa ${ATTEMPT:-1} em $ad com $ocpus OCPU e $memory_gb GB..."
      if out="$(o compute instance launch --compartment-id "$COMPARTMENT" --availability-domain "$ad" \
        --display-name "$NAME" --hostname-label "$NAME" --shape "$SHAPE" \
        --shape-config "{\"ocpus\":$ocpus,\"memoryInGBs\":$memory_gb}" \
        --image-id "$IMAGE" --boot-volume-size-in-gbs "$BOOT_GB" \
        --subnet-id "$SUBNET" --assign-public-ip true \
        --ssh-authorized-keys-file "$SSH_PUBLIC_KEY_FILE" --user-data-file "$USER_DATA_FILE" \
        --wait-for-state RUNNING --max-wait-seconds 1200 --query 'data.id' --raw-output 2>&1)"; then
        LAUNCHED="$(tail -n1 <<< "$out")"
        return 0
      fi
      if grep -qiE 'capacity|TooManyRequests' <<< "$out"; then
        note "  sem vaga agora."
        continue
      fi
      note "$out"
      LAST_ERROR="$(grep -m1 -oE '"message": "[^"]*"' <<< "$out" || tail -n1 <<< "$out")"
      return 1
    done
  done
  return "$NO_CAPACITY"
}

# --- Robô ------------------------------------------------------------------------------------------------------
# O status fica nas tags livres desta VM, que o provision.sh mostra quando é rodado de novo.
report() {
  o compute instance update --instance-id "$SELF" --force --freeform-tags "$(jq -nc \
    --arg when "$(date -u +%FT%TZ)" --arg attempts "$ATTEMPT" --arg result "$1" \
    '{"careflow-ultima-tentativa": $when, "careflow-tentativas": $attempts, "careflow-resultado": $result}')" \
    > /dev/null 2>&1 || note "não consegui gravar o status nas tags (a permissão pode estar propagando)."
}

finish() {
  note "$1"
  report "$1"
  o compute instance terminate --instance-id "$SELF" --preserve-boot-volume false --force > /dev/null ||
    note "não consegui apagar esta VM; o provision.sh apaga na próxima vez que for rodado."
  exit 0
}

loop() {
  : "${DEADLINE_EPOCH:?}" "${INTERVAL_SECONDS:=300}"
  SELF="$(curl -fsS --max-time 10 -H 'Authorization: Bearer Oracle' http://169.254.169.254/opc/v2/instance/id)"
  [[ "$SELF" == ocid1.instance.* ]] || { note "não consegui ler o OCID desta VM no serviço de metadados."; exit 1; }
  ATTEMPT=0
  while true; do
    a1="$(existing_a1 2> /dev/null || true)"
    [[ -z "$a1" ]] || finish "a VM $NAME existe ($a1)."
    (($(date +%s) < DEADLINE_EPOCH)) || finish "o prazo acabou sem vaga."
    ATTEMPT=$((ATTEMPT + 1))
    rc=0
    launch_round || rc=$?
    case "$rc" in
      0) finish "VM $NAME criada ($LAUNCHED)." ;;
      "$NO_CAPACITY") report "sem vaga" ;;
      # Logo depois de criado, o robô ainda não tem permissão (o IAM leva alguns minutos); tenta de novo.
      *) report "erro: ${LAST_ERROR:0:200}" ;;
    esac
    sleep "$INTERVAL_SECONDS"
  done
}

case "${1:-}" in
  once)
    rc=0
    launch_round || rc=$?
    [[ "$rc" -ne 0 ]] || echo "$LAUNCHED"
    exit "$rc"
    ;;
  loop) loop ;;
  *)
    note "uso: $0 once|loop"
    exit 2
    ;;
esac
