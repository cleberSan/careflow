-- Bancos e logins de desenvolvimento local. Roda só na primeira subida do volume do Postgres.
-- Cada serviço tem seu banco. As migrations (papel careflow, dono) criam as tabelas e o papel da
-- aplicação; a API conecta com um login sem privilégio de dono, membro desse papel, então o
-- Row-Level Security vale também em desenvolvimento. Nunca reutilize fora da sua máquina.

CREATE DATABASE careflow_vault OWNER careflow;

CREATE ROLE scheduling_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
CREATE ROLE vault_app NOLOGIN NOSUPERUSER NOBYPASSRLS;

CREATE ROLE scheduling_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE scheduling_app;
CREATE ROLE vault_api LOGIN NOSUPERUSER NOBYPASSRLS IN ROLE vault_app;
