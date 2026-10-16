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
| Patient Vault | Dados pessoais e de saúde cifrados por campo, com chave por paciente |
| Consent | Bases legais, consentimentos e revogação propagada por eventos |
| Triage | Sugestão de prioridade com IA sobre dados pseudonimizados, com humano no circuito |
| Notifications | WhatsApp, SMS e e-mail com fallback entre canais |
| Audit | Trilha de auditoria append-only com hash encadeado |
| Rights | Pedidos do titular (acesso, portabilidade, eliminação) e Bundle HL7 FHIR R4 |

## Roadmap

- [x] **Fase 0:** padrões do repositório, CI com qualidade e segurança, ADRs iniciais
- [x] **Fase 2:** Scheduling MVP com prevenção de dupla reserva ([ADR-0002](docs/adr/0002-agenda-sem-dupla-reserva.md))
- [x] **Fase 3:** Patient Vault com cifragem por campo e Row-Level Security ([ADR-0003](docs/adr/0003-row-level-security-por-clinica.md), [ADR-0004](docs/adr/0004-patient-vault-cifragem-por-campo.md))
- [x] **Fase 4:** Consent e Audit com eventos, Outbox/Inbox e revogação propagada ([ADR-0006](docs/adr/0006-mensageria-outbox-inbox-rabbitmq.md), [ADR-0007](docs/adr/0007-auditoria-com-cadeia-hmac.md), [ADR-0008](docs/adr/0008-consentimento-e-propagacao-para-a-agenda.md))
- [x] **Fase 5:** Notifications com fallback entre canais, circuit breaker e falhas injetáveis ([ADR-0009](docs/adr/0009-notificacoes-com-fallback-e-falhas-injetaveis.md))
- [x] **Fase 6:** OpenTelemetry completo e Painel de Privacidade e Resiliência ([ADR-0010](docs/adr/0010-observabilidade-e-painel-de-privacidade.md))
- [x] **Fase 7:** Triagem com IA sobre dados pseudonimizados, guardrails e revisão humana ([ADR-0011](docs/adr/0011-triagem-com-ia-pseudonimizada.md))
- [x] **Fase 8:** Direitos do titular de ponta a ponta e Bundle HL7 FHIR R4 ([ADR-0012](docs/adr/0012-direitos-do-titular-e-fhir.md))
- [x] **Fase 9:** demonstração hospedada de graça numa VM da Oracle, com HTTPS, proteção contra abuso, deploy aprovado com rollback e backup com restauração ensaiada ([ADR-0013](docs/adr/0013-demonstracao-hospedada-na-oracle.md), [passo a passo](docs/operacao/demonstracao-hospedada.md))
- [ ] **Próximas:** testes de carga e deploy gradual

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
- **Envelope encryption em três níveis:** cada paciente tem a própria chave de dados, embrulhada pela chave da clínica, que é cifrada por uma chave mestra no KMS, atrás de `IKeyManagementService` (implementação local para desenvolvimento e testes). Eliminar o paciente destrói a chave dele (crypto-shredding).
- **Rotação sem parar:** a chave da clínica rotaciona por rota própria, a manutenção migra os registros em lotes com o cofre no ar e versões antigas são destruídas quando nada depende delas.
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
| `GET` | `/v1/patients/{patientRef}/contact` | `vault.contact.read` (token de serviço) |
| `POST` | `/v1/keys/rotation` | `vault.keys` |
| `POST` | `/v1/keys/maintenance?batchSize=` | `vault.keys` |
| `DELETE` | `/v1/keys/{version}` | `vault.keys` |

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

Trilha de auditoria em `src/Services/Audit` ([ADR-0007](docs/adr/0007-auditoria-com-cadeia-hmac.md)). Registra cada leitura e alteração de dado de paciente feita no cofre, no Consent e na agenda, cada aviso enviado ou não enviado pelo Notifications e cada triagem, com o resumo SHA-256 do que saiu para o modelo.

- **Cadeia HMAC por clínica:** cada entrada encadeia a anterior com HMAC-SHA256 e chave fora do banco; a verificação aponta a primeira entrada adulterada, removida ou fora de ordem.
- **Append-only no banco:** trigger recusa `UPDATE`, `DELETE` e `TRUNCATE`; a API só lê e insere.

| Método | Rota | Escopo |
|---|---|---|
| `GET` | `/v1/audit/entries?resourceId=&after=&limit=` | `audit.read` |
| `GET` | `/v1/audit/verification` | `audit.read` |

### Notifications

Avisos ao paciente em `src/Services/Notifications` ([ADR-0009](docs/adr/0009-notificacoes-com-fallback-e-falhas-injetaveis.md)): confirmação ao marcar, lembrete antes do horário e aviso de cancelamento, inclusive quando o motivo é a revogação do consentimento.

- **Fallback entre canais:** WhatsApp, SMS e e-mail na ordem configurada pela clínica, com timeout, retry com jitter e circuit breaker por canal (Polly v8 via `Microsoft.Extensions.Http.Resilience`).
- **Sem mensagem duplicada:** chave de idempotência estável por aviso e canal, e consulta de status no provedor antes de cair para outro canal.
- **Falhas injetáveis:** latência, erro, indisponibilidade e resposta perdida por canal, desligadas por padrão e ligadas por configuração sem reiniciar.
- **Consentimento respeitado:** sem consentimento para `notifications`, o aviso é registrado como não enviado e o motivo vai para a auditoria.
- **Contato só no cofre:** lido na hora do envio com token de serviço por clínica, nunca gravado nem logado; cada leitura e cada envio vão para a auditoria.
- **Provedores simulados:** nenhum envio real; o pipeline de resiliência é o mesmo de produção.
- **Observabilidade:** métricas por canal (envios, falhas, fallbacks, estado do circuito) e `/health/channels` com canal degradado.

| Método | Rota | Escopo |
|---|---|---|
| `GET` | `/v1/appointments/{id}/notifications` | `notifications.read` |
| `GET` | `/v1/settings` | `notifications.admin` |
| `PUT` | `/v1/settings` | `notifications.admin` |

### Triage

Triagem assistida por IA em `src/Services/Triage` ([ADR-0011](docs/adr/0011-triagem-com-ia-pseudonimizada.md)). A queixa em texto livre vira uma sugestão de prioridade (emergência, urgente, pouco urgente, não urgente) com justificativa curta. Não é diagnóstico, e todo caso termina com um profissional confirmando ou corrigindo.

- **Pseudonimização antes da IA:** nome, CPF, contatos, datas, idade, endereço e links viram marcadores; o modelo recebe só o texto mascarado e a faixa etária, sem pseudônimo nem clínica. Se o mascaramento cobrir mais da metade do texto, a queixa não sai.
- **Alarmes por regra:** sinais de alarme rodam sobre o texto original e só sobem a prioridade; o modelo nunca rebaixa um alarme.
- **Guardrails:** saída validada por JSON Schema, justificativa com diagnóstico, remédio ou promessa descartada, prompt injection barrada antes do modelo e contida depois dele.
- **Consentimento próprio:** sem `ai-triage` ativo não há chamada à IA, e a revogação vale para as próximas triagens.
- **Auditoria:** toda sugestão, leitura, confirmação e correção vai para a trilha HMAC, com o SHA-256 do pedido que saiu e nunca o texto.
- **Avaliação no CI sem custo por build:** 57 casos sintéticos reproduzidos a partir de respostas gravadas do Claude Haiku 5.5, com métricas publicadas como artefato; só um workflow manual, com teto de chamadas, chama o modelo.

| Método | Rota | Escopo |
|---|---|---|
| `POST` | `/v1/triages` | `triage.submit` |
| `GET` | `/v1/triages/{id}` | `triage.read` ou `triage.review` |
| `POST` | `/v1/triages/{id}/review` | `triage.review` |
| `GET` | `/v1/patients/{patientRef}/ai-consent` | `triage.read` ou `triage.review` |

### Rights

Pedidos do titular em `src/Services/Rights` ([ADR-0012](docs/adr/0012-direitos-do-titular-e-fhir.md)). Um pedido atravessa os seis serviços que guardam algo do paciente e só termina quando todos respondem.

- **Identidade antes de tudo:** código de 6 dígitos enviado ao contato do cadastro, cifrado para o Notifications no caminho e guardado só como HMAC; 10 minutos e 5 tentativas.
- **Prazo visível:** 15 dias a partir do recebimento, com estados `received`, `in-progress`, `completed` e `denied` com motivo.
- **Pacote do titular:** `leia-me.html` legível, `dados.json` estruturado e `fhir-bundle.json` (Patient, Appointment e Consent, validado contra os perfis base do R4 com o Firely SDK). As seções viajam cifradas para uma chave do próprio pedido; o pacote fica cifrado por 7 dias e sai por um link de 15 minutos e uso único, anônimo e auditado.
- **Eliminação:** crypto-shredding no cofre, cancelamentos e apagamentos nos outros serviços e comprovante com a versão da política de retenção. A trilha de auditoria fica e a cadeia continua íntegra; depois de restaurar um backup, `erasure-replay` reaplica a eliminação.

| Método | Rota | Escopo |
|---|---|---|
| `POST` | `/v1/subject-requests` | `rights.write` |
| `GET` | `/v1/subject-requests/{id}` | `rights.read` |
| `POST` | `/v1/subject-requests/{id}/identity-confirmation` | `rights.write` |
| `POST` | `/v1/subject-requests/{id}/download-links` | `rights.write` |
| `POST` | `/v1/downloads` | anônima, token no corpo |
| `POST` | `/v1/subject-requests/{id}/erasure-replay` | `rights.admin` |

### Observabilidade e Painel

OpenTelemetry nos sete serviços ([ADR-0010](docs/adr/0010-observabilidade-e-painel-de-privacidade.md)):

- **Um trace por fluxo:** o contexto viaja no envelope do Outbox até o Inbox, então uma reserva é um trace só, da API da agenda ao aviso enviado, e uma revogação vai do Consent ao aviso de cancelamento e à auditoria.
- **Sem dado pessoal na telemetria:** redação no serviço e no Collector, baggage de fora descartado, e um teste de ponta a ponta que varre spans, logs e métricas exportados atrás de nome, CPF, telefone e e-mail.
- **Stack local:** Collector, Tempo, Loki, Prometheus e Grafana no docker compose, com dashboards versionados de latência, Outbox, mensagens mortas, propagação da revogação, fallbacks e circuit breaker.
- **Painel de Privacidade e Resiliência:** o serviço `src/Services/Demo` é o backend do Painel, com sessão por cookie `__Host-`, clínica sintética e partição de circuit breaker por visitante, limites de uso e limpeza no fim. O CI grava os seis cenários, triagem com IA e direitos do titular incluídos, e o site reproduz a gravação, com data e commit.

### Mensageria

RabbitMQ com uma camada própria sobre `RabbitMQ.Client` ([ADR-0006](docs/adr/0006-mensageria-outbox-inbox-rabbitmq.md)): Outbox transacional no publicador, Inbox idempotente no consumidor, filas quorum com mensagens mortas e clínica no envelope para o RLS valer no processamento. Testes de ponta a ponta derrubam o broker e provam que nenhum evento se perde nem é aplicado duas vezes.

## Rodando localmente

Veja [CONTRIBUTING.md](CONTRIBUTING.md).

## Licença

[MIT](LICENSE)
