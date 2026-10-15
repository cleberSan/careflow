# ADR-0008: Consentimento por finalidade e propagação para a agenda

- Status: aceito
- Data: 2026-10-15

## Contexto

Dado de saúde é dado sensível (LGPD art. 11) e precisa de base legal para cada finalidade. O titular pode revogar o consentimento a qualquer momento (art. 8º, §5º), e a revogação precisa ter efeito nos serviços que usam o dado. Até a fase 3 a agenda aceitava qualquer `patientRef`; na fase 3 ficou combinado que a validação viria por eventos, para não acoplar a disponibilidade da agenda à do cofre.

## Decisão

- **Serviço `Consent`** com um registro por `(clínica, patientRef, finalidade)`. Finalidades: `scheduling`, `notifications` e `ai-triage`. Cada registro guarda a base legal, a versão do termo aceito e uma versão que cresce a cada mudança.
- **Bases legais.** `consent` e `specific-consent` são revogáveis. `health-protection` (art. 11, II, f: tutela da saúde por profissional de saúde) não depende de consentimento e não é revogável pelo titular; tentar revogar devolve 409.
- **API.** `PUT /v1/patients/{patientRef}/consents/{finalidade}` concede ou atualiza (idempotente: repetir a mesma concessão não gera evento), `POST .../{finalidade}/revoke` revoga e `GET /v1/patients/{patientRef}/consents` lista. Escopos `consent.write` e `consent.read`. Concessões, revogações e leituras vão para a auditoria.
- **Eventos.** `consent.granted` e `consent.revoked` carregam pseudônimo, finalidade e versão. Quem consome aplica só versões maiores que a conhecida, então duplicatas e eventos atrasados são ignorados e uma concessão antiga nunca desfaz uma revogação mais nova.
- **Agenda valida por projeção local (decisão de Cleber).** A agenda mantém `patient_statuses`, alimentada por `patient.registered` (do cofre) e pelos eventos de consentimento da finalidade `scheduling`. A reserva consulta só essa tabela, sem chamada síncrona a outro serviço. Paciente desconhecido devolve 409 `Agenda.PacienteAindaNaoConhecido`; sem base legal, 409 `Agenda.PacienteSemBaseLegal`.
- **Revogação cancela as consultas futuras (decisão de Cleber).** Ao receber a revogação para agendamento, a agenda bloqueia novas reservas e cancela as consultas futuras do paciente com motivo `ConsentRevoked`, cada cancelamento registrado na auditoria com o ator `careflow-scheduling`. Consultas que já começaram não mudam.
- **Sem corrida entre reserva e revogação.** A reserva lê a linha da projeção com `FOR SHARE` e a revogação a atualiza com `FOR UPDATE` antes de procurar as consultas. Ou a reserva termina antes e é cancelada pela revogação, ou espera e encontra o paciente revogado. Um teste roda reservas e revogação ao mesmo tempo e confere que nenhuma consulta futura sobra.

## Alternativas

- **Validação síncrona (agenda chama cofre e Consent a cada reserva):** consistência imediata, mas a agenda para quando qualquer um dos dois para, e a latência soma.
- **Revogação só bloqueia novas reservas:** mais simples, mas deixaria consultas marcadas para alguém que retirou a base legal para agendamento.
- **Consentimento dentro do cofre:** menos um serviço, mas mistura dado pessoal cifrado com uma informação que vários serviços precisam ler e propagar.

## Consequências

- **Consistência eventual.** Logo depois do cadastro ou da concessão, a agenda pode responder 409 por alguns instantes. O cliente repete com a mesma `Idempotency-Key`. Na revogação, existe uma janela curta em que uma reserva ainda é aceita; ela é cancelada quando a revogação chega, e isso é testado.
- Com o broker fora, a revogação espera no Outbox do Consent e é aplicada quando ele volta (ADR-0006). O tempo até a revogação ter efeito vira uma métrica a acompanhar no painel da fase 6.
- A agenda não avisa o paciente sobre o cancelamento. Isso entra com o serviço de notificações, na fase 5, consumindo o mesmo evento.
- Pacientes cadastrados antes da fase 4 não estão na projeção. Como os dados são sintéticos, basta recadastrar; num ambiente real seria preciso republicar os eventos do cofre.
