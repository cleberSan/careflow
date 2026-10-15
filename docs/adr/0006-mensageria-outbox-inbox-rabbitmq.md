# ADR-0006: Mensageria com RabbitMQ, Outbox e Inbox numa camada própria

- Status: aceito
- Data: 2026-10-15

## Contexto

A partir da fase 4 os serviços conversam por eventos: o cofre avisa a agenda que um paciente existe, o Consent avisa concessões e revogações, e todos mandam registros de acesso para a auditoria. Um evento perdido é grave (uma revogação que não chega deixa consultas marcadas sem base legal) e um evento processado duas vezes também (duas entradas na trilha para o mesmo acesso). O broker vai cair em algum momento, e o serviço não pode parar de atender por isso.

A biblioteca de mensageria pesava na decisão. O MassTransit, padrão de mercado em .NET, passou a ter licença comercial a partir da v9, e a v8 recebe correções só até o fim de 2026. Cleber decidiu não depender dele.

## Decisão

- **RabbitMQ com uma camada própria e fina sobre `RabbitMQ.Client` 7** (`CareFlow.BuildingBlocks.Messaging`), cerca de 1.700 linhas, comentários incluídos. Licença MPL/Apache, sem custo, e cada parte (Outbox, Inbox, RLS, retentativa) fica visível no código do portfólio.
- **Topologia.** Um exchange topic `careflow.events`, com o tipo do evento como routing key. Cada serviço consumidor tem uma fila quorum `careflow.{serviço}` com `x-delivery-limit` e fila de mensagens mortas própria. Um exchange alternativo guarda em `careflow.unrouted` o evento que nenhuma fila recebe, em vez de descartá-lo em silêncio. Publicador e consumidor declaram o que usam (idempotente), então a ordem de subida não importa.
- **Outbox no publicador.** O evento é gravado em `outbox_messages` na mesma transação da mudança de negócio (`db.Enqueue`). Um despachante em segundo plano trava um lote com `FOR UPDATE SKIP LOCKED` (várias instâncias não publicam a mesma mensagem), publica com confirmação do broker e só então marca como despachado. Se a publicação falhar, a mensagem ganha nova tentativa com backoff exponencial e jitter, e o lote para (nada de martelar um broker fora). Os ids são UUID v7 estritamente crescentes, então os eventos saem na ordem em que foram criados.
- **Inbox no consumidor.** Cada mensagem é processada numa transação que grava `inbox_messages (consumidor, message_id)` junto com o efeito. Uma mensagem repetida encontra o registro e é descartada; duas entregas simultâneas disputam a chave primária e só uma confirma.
- **Clínica no envelope.** A clínica viaja no cabeçalho `careflow-tenant-id`, nunca no corpo. O consumidor abre um escopo com essa clínica, então o Row-Level Security vale também no processamento de eventos.
- **Papéis no banco.** A API só faz `INSERT` no Outbox, e só na sua clínica. O despachante usa outro login, membro de `{serviço}_relay`, que lê e atualiza Outbox e Inbox de todas as clínicas por uma política própria e não tem acesso às tabelas de negócio.
- **Falhas.** Mensagem inválida (corpo ilegível, sem clínica, versão impossível) vai direto para mensagens mortas. Falha passageira volta para a fila depois de uma espera crescente; depois do limite de entregas, vai para mensagens mortas.
- **Broker fora não derruba o serviço.** A verificação de saúde do broker fica fora da prontidão (`ready`) e aparece como degradada. Os eventos esperam no Outbox.
- **Observabilidade.** `traceparent` atravessa o broker, com spans de publicação e consumo (`CareFlow.Messaging`) e contadores de publicados, falhas, duplicatas e rejeitados. Nenhum log carrega clínica, pseudônimo ou dado pessoal.

## Alternativas

- **MassTransit 8:** maduro e completo, mas fica sem correções no fim de 2026 e a migração para a v9 traria licença paga.
- **Wolverine:** licença MIT e Outbox embutido, mas muito opinativo sobre a forma dos handlers e da persistência, e não trata a clínica por mensagem com RLS do jeito que o CareFlow precisa.
- **Kafka:** ótimo para log de eventos e reprocessamento, mas pesado para o volume da demonstração e sem fila de mensagens mortas nativa.
- **CDC (Debezium) lendo o Outbox:** remove o polling, mas adiciona um componente inteiro à infraestrutura. O polling com sinal local publica em poucos milissegundos depois do commit.

## Consequências

- A entrega é pelo menos uma vez: o despachante pode publicar de novo se cair entre a confirmação do broker e o commit. O Inbox absorve isso.
- Ordem só é garantida por publicador. Consumidores precisam tolerar desordem; o Consent manda a versão do registro para isso (ADR-0008).
- A camada própria é código nosso para manter. Saga, agendamento de mensagens e outros transportes não existem e só entram se uma fase precisar.
- Outbox e Inbox processados são apagados depois de 7 dias (configurável). Reprocessar um evento mais antigo que isso exige republicá-lo de propósito.
- Testes de ponta a ponta sobem PostgreSQL e RabbitMQ reais e cobrem broker fora, duplicatas, desordem e mensagens mortas.
