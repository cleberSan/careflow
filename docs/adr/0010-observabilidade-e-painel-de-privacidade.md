# ADR-0010: Observabilidade ponta a ponta e Painel de Privacidade e Resiliência

- Status: aceito; hospedagem substituída pelo ADR-0013
- Data: 2026-10-15

> Atualização (fase 9): o backend do Painel passou a ser hospedado numa VM Always Free da Oracle, com o replay como fallback automático, e a sessão do Painel virou um token no cabeçalho `Authorization` em vez de cookie. A decisão e os riscos novos estão no [ADR-0013](0013-demonstracao-hospedada-na-oracle.md). O resto deste ADR continua valendo.

## Contexto

Com cinco serviços, um broker e um Outbox, uma reserva atravessa HTTP, banco e fila antes de virar aviso ao paciente. Sem rastreio de ponta a ponta, explicar por que um aviso saiu por SMS ou por que uma consulta foi cancelada exige juntar logs de vários lugares à mão. Ao mesmo tempo, telemetria é um canal clássico de vazamento: um atributo de span, um baggage repassado ou uma linha de log com o telefone do paciente bastam para pôr dado pessoal fora do cofre (ADR-0004).

O portfólio também precisa mostrar isso funcionando para quem visita o site: um paciente sintético, a reserva vista como um trace, o mesmo paciente visto por cada serviço, a revogação propagando e o WhatsApp caindo com fallback para SMS. O site é estático na Vercel (Hobby) e o CareFlow não tem backend hospedado.

## Decisão

### OpenTelemetry em todos os serviços

- Traces, métricas e logs pelo SDK do .NET, exportados por OTLP quando `OTEL_EXPORTER_OTLP_ENDPOINT` está definido. Logs levam `trace_id` e `span_id`, então do span se chega às linhas do mesmo trace.
- **O contexto atravessa o broker:** o publicador grava `traceparent` no envelope ao enfileirar no Outbox; o relay publica com esse contexto e o consumidor abre o span de processamento como filho dele, depois do Inbox. Uma reserva é um trace só, da API da agenda ao aviso enviado; uma revogação é um trace só, do Consent ao aviso de cancelamento e à auditoria.
- **Baggage de fora é descartado** na entrada de cada serviço. Baggage repassado é a forma mais fácil de um cliente injetar dado que depois vira atributo em todo lugar.
- **Spans de banco sem pai** (as varreduras periódicas do Outbox e da fila de avisos) não são gravados: eram a maior parte do volume e não respondem a nenhuma pergunta.

### Sem dado pessoal na telemetria

Três barreiras, da mais importante para a menos:

1. O código do CareFlow não escreve dado pessoal em logs, spans nem métricas: pseudônimos mascarados (`LogMask`), contatos com `ToString` redigido, rotas parametrizadas.
2. Um processador no próprio serviço, antes do exportador, troca GUIDs em caminhos de URL por `{id}`, tira query strings e mascara qualquer valor com forma de e-mail, CPF ou telefone, inclusive o que vem de bibliotecas.
3. O OpenTelemetry Collector repete a redação por padrão de valor e apaga endereço do cliente, cabeçalhos, query string e user agent.

A garantia é um teste de ponta a ponta que roda todos os fluxos com logs em nível Trace e varre tudo o que foi exportado (atributos, eventos, baggage, corpo e atributos de log, rótulos de métricas) atrás do nome, CPF, telefone e e-mail do paciente sintético. O teste foi validado falhando com a redação desligada.

### Stack local (decisão de Cleber: Collector + Grafana)

Perfil `observability` do docker compose, só no loopback: OpenTelemetry Collector, Tempo (traces), Loki (logs), Prometheus 3 com receptor OTLP (métricas) e Grafana com dois dashboards versionados no repositório:

- **Visão geral:** latência p50/p95/p99 por endpoint, erros, profundidade do Outbox, filas e mensagens mortas, atraso de entrega.
- **Privacidade e resiliência:** tempo de propagação da revogação, envios, falhas e fallbacks por canal, estado do circuit breaker por canal e por partição.

Licenças: Collector e Prometheus são Apache 2.0; Grafana, Tempo e Loki são AGPLv3. Usamos as imagens oficiais sem modificação e só como ferramenta de desenvolvimento local; nada delas é distribuído junto com o código do CareFlow (MIT). Se um dia forem oferecidas como serviço a terceiros ou modificadas, a AGPL passa a exigir publicar as modificações, e isso volta para decisão.

### Painel: gravações reais em vez de backend hospedado (decisão de Cleber)

- O site **reproduz gravações** dos quatro cenários feitas pelo CI, com data e commit visíveis. Nada é simulado no navegador: cada número, span e linha de log veio de uma execução real do sistema inteiro (PostgreSQL e RabbitMQ em Testcontainers, provedores de mensagem simulados).
- Um teste de ponta a ponta (`PanelRecordingTests`) percorre os cenários com um paciente sintético só e, quando `CAREFLOW_PANEL_RECORDING` aponta para um caminho, grava o JSON; o CI publica o arquivo como artefato. Ordem: reserva, visões do paciente, queda do WhatsApp e, por último, a revogação (que cancela tudo).
- **Na gravação publicada**, CPF e telefone sintéticos aparecem só com os dois últimos dígitos. O CPF gerado passa na validação de dígitos e poderia coincidir com o de uma pessoa real; um arquivo público fixo não deve correr esse risco. O mesmo teste falha se nome, CPF, telefone ou e-mail aparecerem fora da visão do cofre.
- Sem hospedagem não há custo de nuvem e a escolha entre Azure e AWS continua na fase 9. Fases 7 e 8 aparecem no Painel como "em breve", sem simulação.

### Serviço Demo (BFF do Painel)

Um serviço novo, `CareFlow.Demo.Api`, é o único ponto que o Painel conhece. Ele roda localmente e no CI; hospedá-lo é decisão da fase 9.

- **Sem YARP nem Keycloak (decisão de Cleber).** O Demo chama os serviços com tokens HS256 de um emissor local, curtos (2 minutos), por clínica, audiência e escopos. Os serviços aceitam esse emissor pela configuração padrão do JwtBearer. O Demo inteiro se recusa a subir em `Production`, e o endpoint de limpeza também.
- **Sessão por cookie (decisão de Cleber):** `__Host-careflow-demo`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, valor opaco de 32 bytes aleatórios. O servidor guarda só o SHA-256 do valor. O navegador nunca recebe token nem id de clínica; o site chama o Demo por uma rota de mesma origem.
- **CSRF:** além do `SameSite=Strict`, toda requisição exige o cabeçalho `X-CareFlow-Demo: 1` e, quando há `Origin`, que ela esteja na lista permitida.
- **Limites de uso:** criação de sessão limitada por IP (janela fixa), requisições limitadas por sessão (token bucket), no máximo 5 reservas por sessão, corpo de até 4 KB. Pool cheio responde 503 com `Retry-After`.
- **Isolamento por visitante:** cada sessão ganha uma clínica sintética nova (GUID novo, nunca reaproveitado) e uma partição isolada do circuit breaker no Notifications. Um visitante não enxerga dados, traces nem falhas de outro: a sessão só lê traces que ela mesma criou (sem IDOR por trace id), e o id é validado como 32 dígitos hexadecimais antes de ir ao Tempo ou ao Loki (sem SSRF nem injeção em LogQL).
- **Trace do visitante começa no Demo:** o contexto de trace que chega do navegador é ignorado, então ninguém amarra a própria requisição a um trace alheio.
- **O navegador vê só o que está numa lista de permissão:** nome, tipo, duração, serviço e status dos spans, atributos `http.*`, `messaging.*`, `db.system*` e `careflow.*`, e os eventos de fallback e de circuito. Nada de `url.full`, `db.query.text` ou endereços.

### Partições dinâmicas no Notifications (muda o ADR-0009)

O ADR-0009 previa clínicas isoladas fixas por configuração. Com clínicas novas a cada sessão, isso não serve. Agora o Notifications tem `IsolatedSlots` partições (até 64). O Demo arrenda uma partição para a clínica da sessão (`PUT /v1/fault-injection/partition`, escopo `notifications.admin`, com prazo); um índice único impede duas clínicas na mesma partição (409). Quem arrenda recebe os circuitos zerados. Falha injetada só vale para a clínica com partição ativa (403 sem ela), e partições isoladas ignoram as falhas globais: derrubar o WhatsApp no Painel nunca afeta outra clínica nem a partição compartilhada.

O estado de cada circuito passou a vir das transições que o Polly anuncia (`OnOpened`, `OnHalfOpened`, `OnClosed`, com isolamento manual distinguido). Antes vinha de um `CircuitBreakerStateProvider` compartilhado, que só pode servir a um circuito: quando as primeiras chamadas de uma partição chegavam juntas, o registro de pipelines montava o pipeline duas vezes e a segunda montagem falhava.

### Limpeza dos dados de cada sessão

Ao fim da sessão (ou no vencimento, 15 minutos), o Demo apaga os dados da clínica sintética em Vault, Consent, Scheduling e Notifications por um endpoint de limpeza (`DELETE /v1/demo/clinic-data`, escopo `demo.purge`), habilitado só por `Demo:PurgeEnabled` e recusado em `Production`. A limpeza descobre as tabelas com `tenant_id`, apaga na ordem das chaves estrangeiras numa transação e com o RLS da clínica ativo. A partição só volta ao pool depois que a limpeza deu certo e um resfriamento passou; se falhar, tenta de novo na próxima rodada.

A **auditoria não é apagada**: é append-only com cadeia HMAC (ADR-0007), e apagar quebraria a verificação. Como cada sessão usa uma clínica nova, a trilha antiga nunca aparece para outro visitante.

## Alternativas

- **Backend do Painel hospedado:** demonstração ao vivo, mas custo recorrente, superfície de ataque pública e a escolha de nuvem antecipada. Fica para a fase 9.
- **Simular os cenários no navegador:** barato, mas deixa de provar alguma coisa.
- **YARP como gateway e Keycloak como IdP:** o desenho de produção, mas duas peças grandes para um BFF local que não é hospedado.
- **Token no navegador (SPA com PKCE):** expõe token a XSS; o cookie `__Host-` com `HttpOnly` não.
- **Jaeger em vez de Tempo, ou um SaaS de observabilidade:** Jaeger cobre só traces; SaaS tem custo e manda telemetria para fora.
- **Reaproveitar um conjunto fixo de clínicas de demo:** a trilha de auditoria, que não é apagada, de um visitante apareceria para o seguinte.

## Consequências

- O Demo guarda sessões em memória e roda com uma instância só. Hospedar com várias réplicas troca o armazenamento por um compartilhado e o rate limiting por um distribuído.
- O estado dos circuitos e das falhas continua por processo (ADR-0009): o Painel ao vivo precisa de uma instância do Notifications, ou de falar com cada uma.
- Um evento atrasado que chegue depois da limpeza pode recriar uma linha numa clínica que ninguém mais usa; ela é apagada se a limpeza rodar de novo e nunca é visível a outro visitante.
- A auditoria das sessões de demonstração cresce sem limpeza. Em uso local e no CI é irrelevante; hospedado, pede retenção própria (fase 8).
- A gravação publicada envelhece: o site mostra data e commit, e uma nova gravação é um artefato do CI copiado para o site.
- O emissor HS256 local exige a mesma chave no Demo e nos serviços em desenvolvimento (user secrets). Em produção nem o Demo nem a limpeza sobem.
