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
- [ ] **Fase 2:** Scheduling MVP com prevenção de dupla reserva
- [ ] **Fase 3:** Patient Vault com cifragem por campo e Row-Level Security
- [ ] **Fase 4:** Consent e Audit com Outbox/Inbox
- [ ] **Fase 5:** Notifications com fallback e falhas injetáveis
- [ ] **Fase 6:** OpenTelemetry completo e Painel de Privacidade e Resiliência
- [ ] **Fase 7:** Triagem com IA, pseudonimização e guardrails
- [ ] **Fase 8:** Direitos do titular e adaptador HL7 FHIR
- [ ] **Fase 9:** Testes de carga, IaC e deploy com canary

A fase 1 é o site do portfólio, no repositório [portfolio-web](https://github.com/cleberSan/portfolio-web).

## Rodando localmente

Veja [CONTRIBUTING.md](CONTRIBUTING.md).

## Licença

[MIT](LICENSE)
