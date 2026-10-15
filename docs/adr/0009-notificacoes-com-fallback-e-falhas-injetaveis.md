# ADR-0009: Notificações com fallback entre canais e falhas injetáveis

- Status: aceito
- Data: 2026-10-15

## Contexto

O paciente precisa saber quando uma consulta é marcada, ser lembrado antes do horário e saber quando ela é cancelada, inclusive quando o cancelamento vem da revogação do consentimento para agendamento (pendência do ADR-0008). Provedores de WhatsApp, SMS e e-mail falham, ficam lentos e às vezes aceitam a mensagem e perdem a resposta. Um aviso repetido ou enviado para quem retirou o consentimento é um problema de privacidade, não só de UX. E o contato do paciente é dado pessoal que hoje só existe, cifrado, no cofre (ADR-0004).

## Decisão

### Eventos e projeções

- A agenda publica `appointment.booked` e `appointment.cancelled` (com motivo `requested` ou `consent-revoked`) pelo Outbox, na mesma transação da reserva ou do cancelamento. Só pseudônimos, data e horário.
- O Notifications consome pelo Inbox (ADR-0006) e mantém duas projeções por clínica: o estado da consulta (`Unknown → Booked/Cancelled`, cancelamento é terminal) e o consentimento da finalidade `notifications` (só versões maiores que a conhecida, como no ADR-0008).
- Evento duplicado com outro id, fora de ordem ou cancelamento antes da confirmação não geram aviso a mais: a projeção trava a linha (`INSERT ... ON CONFLICT DO NOTHING` + `SELECT ... FOR UPDATE`) e o índice único `(clínica, consulta, tipo)` impede dois avisos do mesmo tipo.

### Agendamento dos lembretes (decisão de Cleber: scheduler próprio)

- Cada aviso tem uma linha em `notification_jobs` com `due_at`. O lembrete vence em `início − antecedência` (configurável por clínica, padrão 24 h); confirmação e cancelamento vencem na hora.
- Um despachante em segundo plano pega os vencidos com `FOR UPDATE SKIP LOCKED` e um lease (`locked_until`), pelo login do despachante, que só enxerga a fila. O processamento roda com o login da API dentro da clínica do job, então o RLS vale.
- O lease é também o token de posse: o job só é liberado ou reagendado se `locked_until` ainda for o do lease, e um processamento que perdeu o lease não sobrescreve outro.
- Sem Quartz.NET nem Hangfire: um lembrete é só um envio com data, e a fila no Postgres já resolve concorrência sem dependência nova nem impacto de licença.

### Regras avaliadas na hora do envio

Na hora de enviar, nunca na hora de agendar: aviso já encerrado não faz nada; consulta cancelada descarta confirmação e lembrete pendentes (`Superseded`); consulta que já começou expira o aviso (`Expired`); consentimento revogado encerra como `NotSent` com motivo auditado; consentimento ainda desconhecido espera uma janela curta (padrão 5 min) antes de encerrar como `NotSent`, porque o evento de consentimento pode chegar depois do da agenda.

### Contato lido no cofre na hora (decisão de Cleber)

- Endpoint estreito no cofre: `GET /v1/patients/{patientRef}/contact`, escopo `vault.contact.read`, devolve só telefone e e-mail. Cada leitura entra na trilha como `patient.contact.read`, com o Notifications como ator.
- Não existe identidade entre serviços ainda. O Notifications emite um token de serviço ES256 por clínica, válido por 5 minutos e em cache até um minuto antes de expirar. O cofre guarda só a chave pública (quem valida não consegue emitir) e aceita esse esquema apenas na política de contato: o mesmo token com `vault.read` não lê o cadastro inteiro.
- O emissor local é recusado em `Production`, nos dois lados. Na fase 9 ele dá lugar a client credentials do IdP, sem mudar o endpoint nem a política.
- Nenhum contato fica no banco do Notifications. Cofre fora do ar deixa o aviso pendente para a próxima rodada; 404 do cofre encerra como `NotSent` (sem contato).

### Canais, resiliência e idempotência

- Ordem de canais configurável por clínica (padrão WhatsApp, SMS, e-mail). Canal sem endereço do paciente é pulado e registrado.
- Cada canal é um `HttpClient` com `Microsoft.Extensions.Http.Resilience` (Polly v8), de fora para dentro: timeout total, retry exponencial com jitter, circuit breaker por canal e timeout por tentativa. Falhou o canal, o roteador passa para o próximo (fallback) e conta a métrica.
- **Retry sem mensagem duplicada:** cada envio leva `Idempotency-Key` = `{aviso}-{canal}`, estável entre retentativas, rodadas e reprocessamentos. Antes de cair para o próximo canal depois de uma falha ambígua (timeout, resposta perdida), o Notifications consulta o status no provedor por essa chave; se a mensagem foi aceita, conta como entregue.
- Rodadas: se todos os canais falharem, o aviso é reagendado com backoff exponencial e jitter, até 5 rodadas ou o início da consulta, e então encerra como `Failed`, auditado.

### Provedores simulados e falhas injetáveis (decisão de Cleber: nenhum provedor real)

- Os provedores são `HttpMessageHandler`s que imitam uma API de envio: aceitam `Idempotency-Key`, devolvem 202 na primeira vez e 200 com a mesma mensagem nas repetições, e guardam só uma impressão HMAC do destinatário. O pipeline de resiliência roda igual ao de produção; trocar por Twilio, Meta ou SES é um adaptador.
- Falhas injetáveis por canal com as estratégias de chaos do Polly (Simmy): `Latency`, `Error` (503), `Unavailable` (exceção de rede) e `LostResponse` (aceita e perde a resposta). Ficam dentro do pipeline, então passam pelas mesmas defesas que uma falha real. Desligadas por padrão, ligadas só por configuração (`FaultInjection:Enabled`), com recarga sem reiniciar, para o painel da fase 6 derrubar o WhatsApp.

### Sem Redis (decisão de Cleber)

Idempotência fica no Postgres (índice único e chave por canal) e no provedor. O estado do circuit breaker é por instância, como o Polly faz por padrão; com várias instâncias, cada uma abre o seu circuito, o que é aceitável aqui.

### Privacidade e observabilidade

- Texto só com data, horário e nome curto da clínica: nada de nome do paciente nem dado de saúde. O nome da clínica é validado (sem domínios, contra phishing).
- Contato nunca vai para log nem para o banco; tipos com contato têm `ToString` redigido, e um teste percorre os fluxos com logs em nível Trace e varre logs e banco atrás do telefone e do e-mail.
- Auditoria de cada envio (`notification.{tipo}.sent.{canal}`), de cada não envio com o motivo e de cada falha definitiva; a leitura do contato é auditada pelo cofre.
- Métricas por canal: envios, falhas por resultado, fallbacks, avisos não enviados, leituras de contato e estado do circuit breaker (gauge). `/health/channels` responde `Degraded` com o estado de cada circuito quando algum não está fechado.

## Alternativas

- **Cópia cifrada do contato no Notifications:** envia com o cofre fora, mas o cofre teria de publicar contato em eventos ou compartilhar chave, duplicando dado pessoal que a fase 8 precisa apagar.
- **Quartz.NET (Apache 2.0) ou Hangfire (LGPL, recursos pagos):** prontos, mas trazem schema e modelo próprios para algo que a fila já resolve.
- **Redis para circuito distribuído e deduplicação:** mais uma peça de infraestrutura sem requisito que a justifique.
- **Fallback com `AddFallback` do Polly dentro de um único pipeline:** esconderia qual canal entregou e misturaria o circuito dos canais. O roteador explícito registra cada tentativa.

## Consequências

- Risco residual de duplicata entre canais: se o provedor aceitou, perdeu a resposta e também não responde à consulta de status, o próximo canal envia. O paciente pode receber o mesmo aviso por WhatsApp e SMS. Entre retentativas do mesmo canal não há duplicata.
- Com o cofre fora do ar, avisos atrasam (ficam pendentes), não somem nem viram "sem contato".
- A janela de espera pelo consentimento atrasa o aviso de um paciente cujo consentimento ainda não chegou; quem nunca consentiu não recebe nada.
- O estado das falhas injetadas e dos circuitos é por processo. O painel da fase 6 precisa falar com cada instância ou o deploy precisa ter uma só.
- Tokens de serviço locais exigem distribuir o par de chaves em desenvolvimento (user secrets). Em produção o serviço não sobe com o emissor local.
