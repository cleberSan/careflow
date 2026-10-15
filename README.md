# CareFlow

[![CI](https://github.com/cleberSan/careflow/actions/workflows/ci.yml/badge.svg)](https://github.com/cleberSan/careflow/actions/workflows/ci.yml)
[![CodeQL](https://github.com/cleberSan/careflow/actions/workflows/codeql.yml/badge.svg)](https://github.com/cleberSan/careflow/actions/workflows/codeql.yml)
[![Licença: MIT](https://img.shields.io/badge/licen%C3%A7a-MIT-blue.svg)](LICENSE)

Plataforma multi-clínica de agendamento e triagem assistida por IA, com **privacidade por design (LGPD)**, construída em .NET 10 com microsserviços.

> **Projeto de demonstração.** Usa exclusivamente dados sintéticos e não faz diagnóstico nem substitui avaliação clínica.

## Por que este projeto existe

O CareFlow é o projeto vitrine do portfólio de Cleber. Ele é construído em fases públicas, e cada decisão fica registrada em [ADRs](docs/adr), para mostrar não só o resultado, mas o fluxo de engenharia: segurança, testes, observabilidade e resiliência desde o primeiro commit.

## Arquitetura alvo

| Serviço | Responsabilidade |
|---|---|
| Scheduling | Agenda multi-clínica, sem dupla reserva de horário |
| Patient Vault | Dados pessoais e de saúde cifrados por campo, com chave por clínica |
| Consent | Bases legais, consentimentos e revogação propagada por eventos |
| Triage | Sugestão de prioridade com IA sobre dados pseudonimizados, com humano no circuito |
| Notifications | WhatsApp, SMS e e-mail com fallback entre canais |
| Audit | Trilha de auditoria append-only com hash encadeado |

## Roadmap

- [x] **Fase 0:** padrões do repositório, CI com qualidade e segurança, ADRs iniciais
- [x] **Fase 2:** Scheduling MVP com prevenção de dupla reserva ([ADR-0002](docs/adr/0002-agenda-sem-dupla-reserva.md))
- [x] **Fase 3:** Patient Vault com cifragem por campo e Row-Level Security ([ADR-0003](docs/adr/0003-row-level-security-por-clinica.md), [ADR-0004](docs/adr/0004-patient-vault-cifragem-por-campo.md))
- [x] **Fase 4:** Consent e Audit com eventos, Outbox/Inbox e revogação propagada ([ADR-0006](docs/adr/0006-mensageria-outbox-inbox-rabbitmq.md), [ADR-0007](docs/adr/0007-auditoria-com-cadeia-hmac.md), [ADR-0008](docs/adr/0008-consentimento-e-propagacao-para-a-agenda.md))
- [ ] **Fase 5:** Notifications com fallback e falhas injetáveis
- [ ] **Fase 6:** OpenTelemetry completo e Painel de Privacidade e Resiliência
- [ ] **Fase 7:** Triagem com IA, pseudonimização e guardrails
- [ ] **Fase 8:** Direitos do titular e adaptador HL7 FHIR
- [ ] **Fase 9:** Testes de carga, IaC e deploy com canary

A fase 1 é o site do portfólio, no repositório [portfolio-web](https://github.com/cleberSan/portfolio-web).

## Serviços disponíveis

### Scheduling

API de agenda em `src/Services/Scheduling`. Destaques:

- **Sem dupla reserva garantida pelo banco:** exclusion constraint GiST no PostgreSQL, testada com 20 reservas simultâneas do mesmo horário.
- **Idempotência:** `Idempotency-Key` obrigatória na criação; retentativas devolvem a mesma consulta.
- **Multi-clínica:** clínica vinda do token, filtro global por tenant, Row-Level Security no PostgreSQL e 404 para recursos de outra clínica.
- **Paciente validado por eventos:** só reserva para paciente cadastrado no cofre e com base legal para agendamento, conforme a projeção local alimentada por eventos. A revogação cancela as consultas futuras.
- **Erros padronizados:** ProblemDetails (RFC 9457) com código estável e `traceId`.
- **Observabilidade:** OpenTelemetry (traces, métricas de negócio e logs), exportado por OTLP quando `OTEL_EXPORTER_OTLP_ENDPOINT` está definido.

| Método | Rota | Escopo |
|---|---|---|
| `POST` | `/v1/appointments` | `scheduling.write` |
| `GET` | `/v1/appointments/{id}` | `scheduling.read` |
| `POST` | `/v1/appointments/{id}/cancel` | `scheduling.write` |
| `GET` | `/v1/professionals/{id}/appointments?from=&to=` | `scheduling.read` |

### Patient Vault

Cofre de dados pessoais em `src/Services/PatientVault`. Os outros serviços só conhecem o pseudônimo `patientRef`. Destaques:

- **Cifragem por campo:** AES-256-GCM em cada campo pessoal, com clínica, paciente e campo como dado autenticado. Quem lê o banco ou um backup não vê nada em claro.
- **Envelope encryption com chave por clínica:** a chave de dados de cada clínica é cifrada por uma chave mestra no KMS, atrás de `IKeyManagementService` (implementação local para desenvolvimento e testes).
- **Blind index do CPF:** HMAC com chave da clínica, para recusar CPF duplicado e buscar o `patientRef` sem guardar o CPF em claro.
- **Row-Level Security:** a API conecta com um papel sem privilégio de dono e só enxerga linhas da clínica do token.
- **Concorrência otimista:** alterações exigem `If-Match` com o ETag lido.
- **Sem dado pessoal em logs:** verificado por teste que percorre todos os fluxos com logs em nível Trace.

| Método | Rota | Escopo |
|---|---|---|
| `POST` | `/v1/patients` | `vault.write` |
| `GET` | `/v1/patients/{patientRef}` | `vault.read` |
| `PUT` | `/v1/patients/{patientRef}` | `vault.write` + `If-Match` |
| `POST` | `/v1/patients/lookup` | `vault.read` |

### Consent

Consentimentos e bases legais por finalidade em `src/Services/Consent` ([ADR-0008](docs/adr/0008-consentimento-e-propagacao-para-a-agenda.md)).

- **Por finalidade:** agendamento, notificações e triagem por IA, cada uma com base legal e versão do termo.
- **Revogação propagada:** eventos versionados pelo Outbox; a agenda cancela consultas futuras e bloqueia novas reservas.
- **Bases não revogáveis:** tutela da saúde (LGPD art. 11, II, f) não pode ser revogada pelo titular.

| Método | Rota | Escopo |
|---|---|---|
| `PUT` | `/v1/patients/{patientRef}/consents/{purpose}` | `consent.write` |
| `POST` | `/v1/patients/{patientRef}/consents/{purpose}/revoke` | `consent.write` |
| `GET` | `/v1/patients/{patientRef}/consents` | `consent.read` |

### Audit

Trilha de auditoria em `src/Services/Audit` ([ADR-0007](docs/adr/0007-auditoria-com-cadeia-hmac.md)). Registra cada leitura e alteração de dado de paciente feita no cofre, no Consent e na agenda.

- **Cadeia HMAC por clínica:** cada entrada encadeia a anterior com HMAC-SHA256 e chave fora do banco; a verificação aponta a primeira entrada adulterada, removida ou fora de ordem.
- **Append-only no banco:** trigger recusa `UPDATE`, `DELETE` e `TRUNCATE`; a API só lê e insere.

| Método | Rota | Escopo |
|---|---|---|
| `GET` | `/v1/audit/entries?resourceId=&after=&limit=` | `audit.read` |
| `GET` | `/v1/audit/verification` | `audit.read` |

### Mensageria

RabbitMQ com uma camada própria sobre `RabbitMQ.Client` ([ADR-0006](docs/adr/0006-mensageria-outbox-inbox-rabbitmq.md)): Outbox transacional no publicador, Inbox idempotente no consumidor, filas quorum com mensagens mortas e clínica no envelope para o RLS valer no processamento. Testes de ponta a ponta derrubam o broker e provam que nenhum evento se perde nem é aplicado duas vezes.

## Rodando localmente

Veja [CONTRIBUTING.md](CONTRIBUTING.md).

## Licença

[MIT](LICENSE)
