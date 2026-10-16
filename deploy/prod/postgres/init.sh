#!/bin/sh
# Bancos e logins da demonstração hospedada (ADR-0013). Roda só na primeira subida do volume do Postgres.
# Mesmo desenho do init-dev.sql (um banco por serviço, APIs e despachantes sem superusuário, sujeitos ao RLS),
# com duas diferenças: cada login tem a própria senha, gerada na VM por careflow-generate-secrets e entregue só a
# este container, e cada banco tem um dono próprio sem superusuário (<serviço>_owner). É ele que roda as migrations e
# a limpeza das clínicas do Painel, e o FORCE RLS vale para ele também. O superusuário "careflow" fica só para o
# backup e a restauração.
set -eu

for name in SCHEDULING VAULT CONSENT AUDIT NOTIFICATIONS TRIAGE RIGHTS; do
  for kind in OWNER API DISPATCHER; do
    eval "value=\${CAREFLOW_${name}_${kind}_PASSWORD:-}"
    if [ -z "$value" ]; then
      echo "Falta CAREFLOW_${name}_${kind}_PASSWORD em postgres.env" >&2
      exit 1
    fi
  done
done

create_service() {
  service="$1"; database="$2"; owner_password="$3"; api_password="$4"; dispatcher_password="$5"
  # As senhas entram como variáveis do psql (:'nome' cita e escapa); nunca aparecem na linha de comando do processo.
  psql -v ON_ERROR_STOP=1 --username careflow --dbname postgres \
    -v owner_password="$owner_password" -v api_password="$api_password" -v dispatcher_password="$dispatcher_password" <<SQL
CREATE ROLE ${service}_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'owner_password';
CREATE DATABASE ${database} OWNER ${service}_owner;
CREATE ROLE ${service}_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE ${service}_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE ${service}_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE ${service}_app PASSWORD :'api_password';
CREATE ROLE ${service}_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE ${service}_relay PASSWORD :'dispatcher_password';
REVOKE ALL ON DATABASE ${database} FROM PUBLIC;
GRANT CONNECT ON DATABASE ${database} TO ${service}_app, ${service}_relay;
SQL
}

create_service scheduling careflow_scheduling "$CAREFLOW_SCHEDULING_OWNER_PASSWORD" "$CAREFLOW_SCHEDULING_API_PASSWORD" "$CAREFLOW_SCHEDULING_DISPATCHER_PASSWORD"
create_service vault careflow_vault "$CAREFLOW_VAULT_OWNER_PASSWORD" "$CAREFLOW_VAULT_API_PASSWORD" "$CAREFLOW_VAULT_DISPATCHER_PASSWORD"
create_service consent careflow_consent "$CAREFLOW_CONSENT_OWNER_PASSWORD" "$CAREFLOW_CONSENT_API_PASSWORD" "$CAREFLOW_CONSENT_DISPATCHER_PASSWORD"
create_service audit careflow_audit "$CAREFLOW_AUDIT_OWNER_PASSWORD" "$CAREFLOW_AUDIT_API_PASSWORD" "$CAREFLOW_AUDIT_DISPATCHER_PASSWORD"
create_service notifications careflow_notifications "$CAREFLOW_NOTIFICATIONS_OWNER_PASSWORD" "$CAREFLOW_NOTIFICATIONS_API_PASSWORD" "$CAREFLOW_NOTIFICATIONS_DISPATCHER_PASSWORD"
create_service triage careflow_triage "$CAREFLOW_TRIAGE_OWNER_PASSWORD" "$CAREFLOW_TRIAGE_API_PASSWORD" "$CAREFLOW_TRIAGE_DISPATCHER_PASSWORD"
create_service rights careflow_rights "$CAREFLOW_RIGHTS_OWNER_PASSWORD" "$CAREFLOW_RIGHTS_API_PASSWORD" "$CAREFLOW_RIGHTS_DISPATCHER_PASSWORD"
