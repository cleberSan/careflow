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
- [ ] **Fase 4:** Consent e Audit com Outbox/Inbox
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

## Rodando localmente

Veja [CONTRIBUTING.md](CONTRIBUTING.md).

## Licença

[MIT](LICENSE)
