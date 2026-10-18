#!/usr/bin/env bash
# Cria na Oracle Cloud tudo o que a demonstração hospedada precisa (ADR-0013, passos 2 a 8 do passo a passo em
# docs/operacao/demonstracao-hospedada.md): compartimento, rede só com 22 e 443, a VM Ampere A1 com o cloud-init
# deste repositório, o cofre com a chave de software, o bucket de backup com a regra de 7 dias e as permissões
# da VM. Feito para rodar no Cloud Shell do console da Oracle, que já está autenticado como você:
#
#   bash <(curl -fsSL https://raw.githubusercontent.com/cleberSan/careflow/main/deploy/oracle/provision.sh)
#
# Só cria recursos Always Free, com tamanhos fixos: 2 OCPU e 12 GB na A1 (ou 1 OCPU e 6 GB quando falta vaga para
# a maior), disco de 100 GB, vault padrão (não o privado, que é pago), chave protegida por software e um bucket
# Standard. Para antes de criar a VM se a conta já tiver outra instância ou outro disco, para não passar da cota
# gratuita. Pode rodar de novo: o que já existe é reaproveitado pelo nome. Não pede nem guarda senha, token ou
# chave privada da conta.
set -euo pipefail

REF="${CAREFLOW_REF:-main}"
RAW="https://raw.githubusercontent.com/cleberSan/careflow/$REF"
NAME="careflow"
BUCKET="careflow-backups"
SHAPE="VM.Standard.A1.Flex"
# Tamanhos da A1 em "OCPU:GB", do preferido ao menor aceito. A menor costuma ter vaga quando a maior não tem, e a
# stack cabe nela com o swap do cloud-init; dá para crescer depois (docs/operacao/demonstracao-hospedada.md).
SIZES=("2:12" "1:6")
BOOT_GB=100
VCN_CIDR="10.0.0.0/16"
SUBNET_CIDR="10.0.0.0/24"
SSH_KEY="$HOME/.ssh/careflow-oracle"
SUMMARY="$HOME/careflow-oracle.env"
# Por quanto tempo insistir quando a Oracle está sem vaga para a A1 gratuita.
RETRY_MINUTES="${CAREFLOW_RETRY_MINUTES:-15}"
[[ "$RETRY_MINUTES" =~ ^[0-9]+$ ]] || { echo "CAREFLOW_RETRY_MINUTES precisa ser um número de minutos." >&2; exit 1; }

log() { printf '\n== %s\n' "$*"; }
die() { printf '\nERRO: %s\n' "$*" >&2; exit 1; }

command -v oci > /dev/null || die "não achei a CLI da Oracle. Rode este script no Cloud Shell do console da Oracle."
command -v jq > /dev/null || die "não achei o jq. Rode este script no Cloud Shell do console da Oracle."
# O Cloud Shell exporta OCI_TENANCY; fora dele, vale o tenancy do arquivo de configuração da CLI.
TENANCY="${OCI_TENANCY:-$(sed -n 's/^tenancy[[:space:]]*=[[:space:]]*//p' "${OCI_CLI_CONFIG_FILE:-$HOME/.oci/config}" 2> /dev/null | head -n1)}"
[[ "$TENANCY" == ocid1.tenancy.* ]] || die "não achei o OCID do tenancy. Rode este script no Cloud Shell do console da Oracle."

# Os recursos Always Free só existem na região principal, e as mudanças de IAM também são feitas nela.
REGION="$(oci iam region-subscription list --tenancy-id "$TENANCY" \
  --query 'data[?"is-home-region"] | [0]."region-name"' --raw-output)"
[[ -n "$REGION" && "$REGION" != null ]] || die "não consegui descobrir a região principal da conta."
o() { oci --region "$REGION" "$@"; }

log "Região principal: $REGION"

# --- Compartimento -------------------------------------------------------------------------------------------
log "Compartimento $NAME"
COMPARTMENT="$(o iam compartment list --compartment-id "$TENANCY" --name "$NAME" --lifecycle-state ACTIVE \
  --query 'data[0].id' --raw-output 2> /dev/null || true)"
if [[ -z "$COMPARTMENT" || "$COMPARTMENT" == null ]]; then
  COMPARTMENT="$(o iam compartment create --compartment-id "$TENANCY" --name "$NAME" \
    --description "Demonstração hospedada do CareFlow" --wait-for-state ACTIVE --query 'data.id' --raw-output)"
  # O compartimento novo leva alguns segundos para aparecer nos outros serviços.
  sleep 20
fi
echo "$COMPARTMENT"

# --- Rede: VCN, internet gateway, rota e security list só com 22 e 443 ----------------------------------------
log "Rede"
VCN="$(o network vcn list --compartment-id "$COMPARTMENT" --display-name "$NAME" --lifecycle-state AVAILABLE \
  --query 'data[0].id' --raw-output 2> /dev/null || true)"
if [[ -z "$VCN" || "$VCN" == null ]]; then
  VCN="$(o network vcn create --compartment-id "$COMPARTMENT" --display-name "$NAME" --cidr-blocks "[\"$VCN_CIDR\"]" \
    --dns-label careflow --wait-for-state AVAILABLE --query 'data.id' --raw-output)"
fi

IGW="$(o network internet-gateway list --compartment-id "$COMPARTMENT" --vcn-id "$VCN" --display-name "$NAME" \
  --lifecycle-state AVAILABLE --query 'data[0].id' --raw-output 2> /dev/null || true)"
if [[ -z "$IGW" || "$IGW" == null ]]; then
  IGW="$(o network internet-gateway create --compartment-id "$COMPARTMENT" --vcn-id "$VCN" --display-name "$NAME" \
    --is-enabled true --wait-for-state AVAILABLE --query 'data.id' --raw-output)"
fi

ROUTE_TABLE="$(o network vcn get --vcn-id "$VCN" --query 'data."default-route-table-id"' --raw-output)"
o network route-table update --rt-id "$ROUTE_TABLE" --force --wait-for-state AVAILABLE \
  --route-rules "[{\"destination\":\"0.0.0.0/0\",\"destinationType\":\"CIDR_BLOCK\",\"networkEntityId\":\"$IGW\"}]" > /dev/null

# Entrada: SSH (só chave; o deploy vem dos runners do GitHub, que mudam de IP), 443 e o ICMP que o próprio
# assistente da Oracle cria para o path MTU. A porta 80 fica fechada: o certificado sai pela 443 (TLS-ALPN).
INGRESS='[
  {"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":22,"max":22}}},
  {"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":443,"max":443}}},
  {"protocol":"1","source":"0.0.0.0/0","icmpOptions":{"type":3,"code":4}},
  {"protocol":"1","source":"'"$VCN_CIDR"'","icmpOptions":{"type":3}}
]'
EGRESS='[{"protocol":"all","destination":"0.0.0.0/0"}]'
SECURITY_LIST="$(o network security-list list --compartment-id "$COMPARTMENT" --vcn-id "$VCN" --display-name "$NAME" \
  --lifecycle-state AVAILABLE --query 'data[0].id' --raw-output 2> /dev/null || true)"
if [[ -z "$SECURITY_LIST" || "$SECURITY_LIST" == null ]]; then
  SECURITY_LIST="$(o network security-list create --compartment-id "$COMPARTMENT" --vcn-id "$VCN" --display-name "$NAME" \
    --ingress-security-rules "$INGRESS" --egress-security-rules "$EGRESS" --wait-for-state AVAILABLE \
    --query 'data.id' --raw-output)"
else
  o network security-list update --security-list-id "$SECURITY_LIST" --force --wait-for-state AVAILABLE \
    --ingress-security-rules "$INGRESS" --egress-security-rules "$EGRESS" > /dev/null
fi

SUBNET="$(o network subnet list --compartment-id "$COMPARTMENT" --vcn-id "$VCN" --display-name "$NAME" \
  --lifecycle-state AVAILABLE --query 'data[0].id' --raw-output 2> /dev/null || true)"
if [[ -z "$SUBNET" || "$SUBNET" == null ]]; then
  SUBNET="$(o network subnet create --compartment-id "$COMPARTMENT" --vcn-id "$VCN" --display-name "$NAME" \
    --cidr-block "$SUBNET_CIDR" --dns-label publica --route-table-id "$ROUTE_TABLE" \
    --security-list-ids "[\"$SECURITY_LIST\"]" --prohibit-public-ip-on-vnic false \
    --wait-for-state AVAILABLE --query 'data.id' --raw-output)"
fi
echo "VCN, gateway, rota e sub-rede prontos. Entrada só pelas portas 22 e 443."

# --- Chave SSH (fica só no seu Cloud Shell; a VM recebe a pública) ---------------------------------------------
if [[ ! -f "$SSH_KEY" ]]; then
  log "Chave SSH em $SSH_KEY"
  mkdir -p "$HOME/.ssh" && chmod 0700 "$HOME/.ssh"
  # O Cloud Shell roda em modo FIPS, que recusa ed25519; RSA de 4096 bits é aceito por ele e pela VM.
  ssh-keygen -q -t rsa -b 4096 -N "" -C "careflow-cloud-shell" -f "$SSH_KEY"
fi

# --- VM ------------------------------------------------------------------------------------------------------
log "VM $NAME ($SHAPE, 2 OCPU e 12 GB, ou 1 OCPU e 6 GB se faltar vaga; disco de $BOOT_GB GB)"
# shellcheck disable=SC2016 # os acentos graves são literais do JMESPath, não expansão do shell
INSTANCE="$(o compute instance list --compartment-id "$COMPARTMENT" --display-name "$NAME" --all \
  --query 'data[?"lifecycle-state"!=`TERMINATED` && "lifecycle-state"!=`TERMINATING`] | [0].id' --raw-output)"
if [[ -z "$INSTANCE" || "$INSTANCE" == null ]]; then
  # Cota gratuita: até 2 OCPU e 12 GB na A1 e 200 GB de disco somando tudo. Para não passar dela sem perceber,
  # a VM só é criada numa conta sem outras instâncias ou discos.
  others="$(o search resource structured-search \
    --query-text "query instance, bootvolume, volume resources where lifeCycleState != 'TERMINATED' && lifeCycleState != 'TERMINATING'" \
    --query 'data.items[].["resource-type", "display-name"]' --output json | jq -r '.[] | join(": ")')"
  if [[ -n "$others" ]]; then
    printf '%s\n' "$others"
    die "a conta já tem as instâncias ou discos acima. Para não passar da cota gratuita, apague-os ou fale comigo antes de continuar."
  fi

  IMAGE="$(o compute image list --compartment-id "$COMPARTMENT" --operating-system "Canonical Ubuntu" \
    --operating-system-version "24.04" --shape "$SHAPE" --sort-by TIMECREATED --sort-order DESC --limit 1 \
    --query 'data[0].id' --raw-output 2> /dev/null)"
  [[ "$IMAGE" == ocid1.image.* ]] || die "não achei a imagem Ubuntu 24.04 para ARM."

  cloud_init="$(mktemp)"
  trap 'rm -f "$cloud_init"' EXIT
  curl -fsSL "$RAW/deploy/oracle/cloud-init.yaml" -o "$cloud_init"
  head -n1 "$cloud_init" | grep -q '^#cloud-config' || die "o cloud-init baixado não parece válido."

  # "Out of host capacity" é comum na A1 gratuita, ainda mais em região com um domínio só, como São Paulo: tenta
  # cada domínio e cada tamanho e repete a cada minuto até o prazo. O Cloud Shell fecha depois de uns 20 minutos
  # sem uso.
  mapfile -t ads < <(o iam availability-domain list --compartment-id "$TENANCY" --query 'data[].name' --output json | jq -r '.[]')
  deadline=$((SECONDS + RETRY_MINUTES * 60))
  attempt=0
  while [[ "$INSTANCE" != ocid1.instance.* ]]; do
    attempt=$((attempt + 1))
    for ad in "${ads[@]}"; do
      for size in "${SIZES[@]}"; do
        ocpus="${size%%:*}" memory_gb="${size##*:}"
        echo "$(date +%H:%M) tentativa $attempt em $ad com $ocpus OCPU e $memory_gb GB..."
        if out="$(o compute instance launch --compartment-id "$COMPARTMENT" --availability-domain "$ad" \
          --display-name "$NAME" --hostname-label "$NAME" --shape "$SHAPE" \
          --shape-config "{\"ocpus\":$ocpus,\"memoryInGBs\":$memory_gb}" \
          --image-id "$IMAGE" --boot-volume-size-in-gbs "$BOOT_GB" \
          --subnet-id "$SUBNET" --assign-public-ip true \
          --ssh-authorized-keys-file "$SSH_KEY.pub" --user-data-file "$cloud_init" \
          --wait-for-state RUNNING --max-wait-seconds 1200 --query 'data.id' --raw-output 2>&1)"; then
          INSTANCE="$(tail -n1 <<< "$out")"
          break 2
        fi
        if grep -qiE 'capacity|TooManyRequests' <<< "$out"; then
          echo "  sem vaga agora."
          continue
        fi
        printf '%s\n' "$out" >&2
        die "a Oracle recusou a criação da VM (mensagem acima)."
      done
    done
    [[ "$INSTANCE" != ocid1.instance.* ]] || break
    if ((SECONDS + 60 > deadline)); then
      die "a Oracle ficou sem vaga gratuita de A1 durante $RETRY_MINUTES minutos. Rode o mesmo comando de novo mais tarde (de madrugada costuma ter mais vaga); o que já foi criado é reaproveitado."
    fi
    sleep 60
  done
fi
PUBLIC_IP="$(o compute instance list-vnics --instance-id "$INSTANCE" --query 'data[0]."public-ip"' --raw-output)"
echo "$INSTANCE"
echo "IP público: $PUBLIC_IP"
VM_SIZE="$(o compute instance get --instance-id "$INSTANCE" \
  --query 'data."shape-config".[ocpus, "memory-in-gbs"]' --output json | jq -r '"\(.[0] | floor):\(.[1] | floor)"')"
echo "Tamanho: ${VM_SIZE%%:*} OCPU e ${VM_SIZE##*:} GB"

# --- Cofre e chave de software --------------------------------------------------------------------------------
log "Cofre (vault padrão, gratuito) e chave careflow-kek (software)"
VAULT="$(o kms management vault list --compartment-id "$COMPARTMENT" --all \
  --query "data[?\"display-name\"=='$NAME' && \"lifecycle-state\"=='ACTIVE'] | [0].id" --raw-output)"
if [[ -z "$VAULT" || "$VAULT" == null ]]; then
  echo "Criando o vault. A Oracle costuma levar alguns minutos."
  VAULT="$(o kms management vault create --compartment-id "$COMPARTMENT" --display-name "$NAME" --vault-type DEFAULT \
    --wait-for-state ACTIVE --max-wait-seconds 1800 --query 'data.id' --raw-output)"
fi
vault_json="$(o kms management vault get --vault-id "$VAULT" --query 'data' --output json)"
[[ "$(jq -r '."vault-type"' <<< "$vault_json")" == DEFAULT ]] || die "o vault $NAME não é do tipo padrão (o privado é pago)."
MANAGEMENT_ENDPOINT="$(jq -r '."management-endpoint"' <<< "$vault_json")"
CRYPTO_ENDPOINT="$(jq -r '."crypto-endpoint"' <<< "$vault_json")"

KEY="$(o kms management key list --compartment-id "$COMPARTMENT" --endpoint "$MANAGEMENT_ENDPOINT" --all \
  --query "data[?\"display-name\"=='careflow-kek' && \"lifecycle-state\"=='ENABLED'] | [0].id" --raw-output)"
if [[ -z "$KEY" || "$KEY" == null ]]; then
  KEY="$(o kms management key create --compartment-id "$COMPARTMENT" --endpoint "$MANAGEMENT_ENDPOINT" \
    --display-name careflow-kek --key-shape '{"algorithm":"AES","length":32}' --protection-mode SOFTWARE \
    --wait-for-state ENABLED --max-wait-seconds 900 --query 'data.id' --raw-output)"
fi
echo "$KEY"

# --- Permissões da VM: grupo dinâmico com só esta instância e políticas restritas ao compartimento ----------------
log "Permissões da VM (instance principal)"
RULE="instance.id = '$INSTANCE'"
DYNAMIC_GROUP="$(o iam dynamic-group list --compartment-id "$TENANCY" --name "$NAME-vm" \
  --query 'data[0].id' --raw-output 2> /dev/null || true)"
if [[ -z "$DYNAMIC_GROUP" || "$DYNAMIC_GROUP" == null ]]; then
  if ! o iam dynamic-group create --compartment-id "$TENANCY" --name "$NAME-vm" \
    --description "VM da demonstração hospedada do CareFlow" --matching-rule "$RULE" \
    --wait-for-state ACTIVE > /dev/null 2>&1; then
    # Contas novas usam domínios de identidade; o grupo vai para o domínio Default.
    domain_url="$(o iam domain list --compartment-id "$TENANCY" --display-name Default --query 'data[0].url' --raw-output)"
    [[ "$domain_url" == https://* ]] || die "não consegui criar o grupo dinâmico $NAME-vm."
    existing="$(o identity-domains dynamic-resource-groups list --endpoint "$domain_url" \
      --filter "displayName eq \"$NAME-vm\"" --query 'data.resources[0].id' --raw-output 2> /dev/null || true)"
    if [[ -z "$existing" || "$existing" == null ]]; then
      o identity-domains dynamic-resource-group create --endpoint "$domain_url" --display-name "$NAME-vm" \
        --description "VM da demonstração hospedada do CareFlow" --matching-rule "$RULE" \
        --schemas '["urn:ietf:params:scim:schemas:oracle:idcs:DynamicResourceGroup"]' > /dev/null
    fi
  fi
else
  o iam dynamic-group update --dynamic-group-id "$DYNAMIC_GROUP" --matching-rule "$RULE" --force > /dev/null
fi

upsert_policy() {
  local name="$1" description="$2" statements="$3" id
  id="$(o iam policy list --compartment-id "$COMPARTMENT" --name "$name" --query 'data[0].id' --raw-output 2> /dev/null || true)"
  if [[ -z "$id" || "$id" == null ]]; then
    o iam policy create --compartment-id "$COMPARTMENT" --name "$name" --description "$description" \
      --statements "$statements" --wait-for-state ACTIVE > /dev/null
  else
    o iam policy update --policy-id "$id" --description "$description" --statements "$statements" \
      --version-date "" --force > /dev/null
  fi
}

upsert_policy "$NAME-vm" "A VM usa só a chave careflow-kek e o bucket de backup" "$(jq -nc \
  --arg key "$KEY" --arg bucket "$BUCKET" --arg c "$NAME" --arg g "$NAME-vm" '[
    "Allow dynamic-group \($g) to use keys in compartment \($c) where target.key.id = '"'"'\($key)'"'"'",
    "Allow dynamic-group \($g) to read buckets in compartment \($c) where target.bucket.name = '"'"'\($bucket)'"'"'",
    "Allow dynamic-group \($g) to manage objects in compartment \($c) where target.bucket.name = '"'"'\($bucket)'"'"'"
  ]')"
# Sem esta, a regra de ciclo de vida do bucket não consegue apagar os backups antigos.
upsert_policy "$NAME-lifecycle" "O Object Storage apaga os backups com mais de 7 dias" \
  "$(jq -nc --arg r "$REGION" --arg c "$NAME" '["Allow service objectstorage-\($r) to manage object-family in compartment \($c)"]')"

# --- Bucket de backup com a regra de 7 dias ----------------------------------------------------------------------
log "Bucket $BUCKET (privado, apaga depois de 7 dias)"
NAMESPACE="$(o os ns get --query data --raw-output)"
if ! o os bucket get --namespace-name "$NAMESPACE" --bucket-name "$BUCKET" > /dev/null 2>&1; then
  o os bucket create --namespace-name "$NAMESPACE" --compartment-id "$COMPARTMENT" --name "$BUCKET" \
    --public-access-type NoPublicAccess --storage-tier Standard > /dev/null
fi
lifecycle='[{"name":"apagar-depois-de-7-dias","action":"DELETE","timeAmount":7,"timeUnit":"DAYS","isEnabled":true,"target":"objects"}]'
# A política acima pode levar um minuto para valer.
for attempt in 1 2 3 4 5 6; do
  if o os object-lifecycle-policy put --namespace-name "$NAMESPACE" --bucket-name "$BUCKET" \
    --items "$lifecycle" --force > /dev/null 2>&1; then
    break
  fi
  [[ "$attempt" -lt 6 ]] || die "não consegui criar a regra de 7 dias no bucket. Rode o comando de novo em alguns minutos."
  sleep 20
done

# --- Resumo ------------------------------------------------------------------------------------------------------
cat > "$SUMMARY" << EOF
# Gerado por deploy/oracle/provision.sh. Nada aqui é segredo; são os dados que o careflow-setup pede no passo 10.
OCI_REGION=$REGION
PUBLIC_IP=$PUBLIC_IP
INSTANCE_OCID=$INSTANCE
OCI_NAMESPACE=$NAMESPACE
OCI_BUCKET=$BUCKET
VAULT_CRYPTO_ENDPOINT=$CRYPTO_ENDPOINT
KEY_OCID=$KEY
EOF

log "Pronto"
cat "$SUMMARY"
cat << EOF

Esses dados ficaram salvos em $SUMMARY, no seu Cloud Shell.

Próximo passo: o DuckDNS (passo 9), apontando o subdomínio para $PUBLIC_IP. Depois, para entrar na VM daqui
mesmo do Cloud Shell:

  ssh -i $SSH_KEY ubuntu@$PUBLIC_IP
EOF
if [[ "$VM_SIZE" != "${SIZES[0]}" ]]; then
  cat << EOF

A VM saiu com ${VM_SIZE%%:*} OCPU e ${VM_SIZE##*:} GB porque faltou vaga para a de ${SIZES[0]%%:*} OCPU e ${SIZES[0]##*:} GB. A demonstração roda
assim, só um pouco mais devagar. Para crescer depois, veja "Crescer a VM" em docs/operacao/demonstracao-hospedada.md.
EOF
fi
