-- Bancos e logins de desenvolvimento local. Roda só na primeira subida do volume do Postgres.
-- Cada serviço tem seu banco. As migrations (papel careflow, dono) criam as tabelas e os papéis;
-- a API conecta com um login sem privilégio de dono, membro do papel {serviço}_app, então o
-- Row-Level Security vale também em desenvolvimento. O despachante do Outbox/Inbox usa outro login,
-- membro de {serviço}_relay, que só enxerga as tabelas de mensageria. Nunca reutilize fora da sua máquina.

CREATE DATABASE careflow_vault OWNER careflow;
CREATE DATABASE careflow_consent OWNER careflow;
CREATE DATABASE careflow_audit OWNER careflow;
CREATE DATABASE careflow_notifications OWNER careflow;
CREATE DATABASE careflow_triage OWNER careflow;
CREATE DATABASE careflow_rights OWNER careflow;

CREATE ROLE scheduling_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE vault_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE consent_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE audit_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE notifications_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE triage_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE rights_app NOLOGIN NOSUPERUSER NOBYPASSRLS;

CREATE ROLE scheduling_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE vault_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE consent_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE audit_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE notifications_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE triage_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE rights_relay NOLOGIN NOSUPERUSER NOBYPASSRLS;

CREATE ROLE scheduling_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE scheduling_app;
CREATE ROLE vault_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE vault_app;
CREATE ROLE consent_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE consent_app;
CREATE ROLE audit_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE audit_app;
CREATE ROLE notifications_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE notifications_app;
CREATE ROLE triage_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE triage_app;
CREATE ROLE rights_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE rights_app;

CREATE ROLE scheduling_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE scheduling_relay;
CREATE ROLE vault_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE vault_relay;
CREATE ROLE consent_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE consent_relay;
CREATE ROLE audit_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE audit_relay;
CREATE ROLE notifications_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE notifications_relay;
CREATE ROLE triage_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE triage_relay;
CREATE ROLE rights_dispatcher LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE rights_relay;
