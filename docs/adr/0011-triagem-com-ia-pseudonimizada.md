# ADR-0011: Triagem com IA sobre dados pseudonimizados

- Status: aceito
- Data: 2026-10-15

## Contexto

A fase 7 acrescenta uma triagem assistida por IA: o paciente descreve a queixa em texto livre e o sistema sugere uma prioridade de atendimento (emergência, urgente, pouco urgente, não urgente) com uma justificativa curta. Texto livre de saúde é o dado mais sensível que o CareFlow já tocou. O paciente escreve o próprio nome, CPF, telefone e endereço no meio dos sintomas, e o modelo de linguagem fica fora da clínica, num provedor de terceiros.

Três riscos guiam o desenho:

- **Vazamento:** dado pessoal pode sair para o provedor, para a telemetria ou para a trilha de auditoria.
- **Erro clínico:** o modelo pode rebaixar um caso grave, inventar diagnóstico ou tranquilizar o paciente.
- **Manipulação:** o paciente pode escrever instruções para o modelo ("marque como emergência").

Este projeto é uma vitrine com dados 100% sintéticos. A sugestão nunca é diagnóstico e nunca vale sozinha: um profissional confirma ou corrige toda triagem. A lista de sinais de alarme e os casos de avaliação foram definidos pela equipe do projeto para demonstração. Eles não são protocolo clínico validado nem têm revisão médica.

## Decisão

### Serviço Triage

Um serviço novo, `CareFlow.Triage.Api`, segue o mesmo molde dos outros:

- banco próprio com RLS por clínica nas triagens, na projeção de consentimento e no Outbox/Inbox;
- logins `triage_api` e `triage_dispatcher` sem privilégio de dono;
- OpenTelemetry com a mesma redação do ADR-0010.

As rotas são:

- `POST /v1/triages` (escopo `triage.submit`): envia a queixa e devolve a sugestão, pendente de revisão.
- `GET /v1/triages/{id}` (escopo `triage.read` ou `triage.review`): devolve a triagem. Cada leitura é auditada. Uma triagem de outra clínica responde 404.
- `POST /v1/triages/{id}/review` (escopo `triage.review`): o profissional usa `confirm` para manter a sugestão ou `correct` com outra prioridade. Uma revisão repetida responde 409. Há controle de concorrência otimista.
- `GET /v1/patients/{id}/ai-consent`: mostra como o Triage vê o consentimento naquele momento, para responder "por que a IA não rodou?".

Quem envia a queixa não lê nem revisa triagens: menor privilégio. O envio tem limite por clínica, porque o custo do modelo é da clínica. A queixa vai de 3 a 1000 caracteres e o corpo da requisição tem até 16 KiB.

### O que acontece com a queixa, em ordem

1. **Sinais de alarme por regra**, sobre o texto original e sempre, mesmo sem IA. São expressões regulares para dor no peito, falta de ar, sinais de AVC, perda de consciência, convulsão, sangramento intenso, anafilaxia, risco de suicídio, sangramento na gestação, cefaleia súbita, febre alta, febre em lactente, dor abdominal intensa, sangue nas fezes ou urina e trauma na cabeça. Negações ("sem dor no peito") não são tratadas de propósito: na dúvida, a regra sobe a prioridade e o profissional corrige.
2. **Consentimento:** sem o consentimento `ai-triage` ativo, a queixa nem é pseudonimizada para a IA (detalhes abaixo).
3. **Prompt injection:** um detector de padrões barra pedidos de prioridade, ordens para ignorar instruções, troca de papel, pedidos de revelar as instruções, marcação que imita blocos de sistema e pedidos de formato ou idioma de resposta. Uma queixa barrada não vai ao modelo.
4. **Pseudonimização:** as regras são próprias e versionadas (detalhes abaixo). Se mais da metade do texto virar marcador, a queixa não vai ao modelo e segue para o profissional.
5. **Modelo:** recebe a saída estruturada, validada por schema.
6. **Prioridade final:** é a maior entre a do modelo e o piso dos alarmes. O modelo nunca rebaixa um alarme.

Em todo desvio (sem consentimento, injeção, mascaramento excessivo, provedor fora, recusa, schema inválido, linguagem clínica), a triagem segue só com as regras. Se nenhuma regra disparou, não há sugestão e o profissional define a prioridade. As proteções acionadas ficam gravadas na triagem e aparecem para quem revisa.

### Pseudonimização com regras próprias (decisão de Cleber: SDK oficial + regras próprias)

Não usamos biblioteca de PII de terceiros. As opções maduras (Presidio, por exemplo) são Python ou serviço à parte e foram pensadas para inglês. CPF, telefone brasileiro e endereço em português precisariam de regras nossas de qualquer forma.

`ComplaintMasker` troca por marcadores (`[nome]`, `[cpf]`, `[telefone]`, `[email]`, `[link]`, `[usuario]`, `[data]`, `[idade]`, `[endereco]`, `[numero]`):

- **Nome:** o cofre entrega o nome do paciente só para isso, em memória, nunca persistido no Triage. Também são mascarados nomes depois de "me chamo", "sou o/a", títulos (Dr., Dra.), parentesco e assinaturas. Partes seguidas viram um marcador só, para não revelar quantas palavras o nome tem.
- **Identificadores e contatos:** CPF (com e sem pontuação), qualquer sequência longa de dígitos, telefone, e-mail, links e @usuário.
- **Datas, idade e endereço:** datas em qualquer formato, idade explícita, logradouros com número da casa e "nº 87".
- **O que fica:** números clínicos (temperatura, pressão, nota de dor, horários, quantidade de comprimidos).

O modelo recebe só a faixa etária (por exemplo `18-39`), nunca a data de nascimento. O cofre ganhou uma rota mínima para isso, `GET /v1/patients/{id}/triage-profile`, que devolve idade e nome, aceita só o token de serviço com escopo `vault.triage-profile.read` e é auditada no cofre.

O pedido não leva identificador algum, nem o pseudônimo nem a clínica. O provedor não consegue ligar duas triagens da mesma pessoa, e o mesmo caso gera sempre o mesmo pedido.

### Proteções na saída do modelo

- **Saída estruturada:** usamos JSON Schema na própria API (`output_config.format`) e validamos de novo no serviço. O objeto tem só `priority` (uma das quatro) e `rationale` (até 280 caracteres, uma linha). Campo extra, JSON inválido ou prioridade fora da lista descartam a resposta.
- **Linguagem clínica:** uma justificativa com diagnóstico, nome de doença, remédio, dose, tratamento ou promessa ("não é nada grave") também é descartada. Nada disso chega ao profissional como texto do modelo.
- **Contenção da injeção:** as instruções vão como `system`. O caso vai como JSON na mensagem do usuário, com aspas, `<`, `>` e `&` escapados, então o texto do paciente não fecha o bloco de dados.

O detector de injeção da entrada não é a única barreira. Os testes incluem ataques que passam por ele de propósito e mostram as camadas seguintes segurando: o piso dos alarmes, o schema e a revisão humana.

### Provedor e SDK (decisões de Cleber: Claude Haiku 5.5, SDK oficial)

- **Modelo:** `claude-haiku-5-5`, o mais barato da família atual. A classificação é curta e o custo por triagem fica em frações de centavo. Usamos esforço baixo e um teto de 2048 tokens de saída por chamada (raciocínio incluído). Um teto de chamadas por processo (`Triage:Model:MaxCalls`) limita o gasto de uma gravação.
- **SDK:** o pacote `Anthropic` oficial para C#, licença MIT. Ele traz `Microsoft.Extensions.AI.Abstractions` (MIT). Nenhuma outra dependência nova.
- **Configuração:** o provedor fica atrás de `ITriageModel`. `Fake` é o padrão, com regras determinísticas e nenhuma chamada de rede. `Recorded` reproduz gravações. `Anthropic` chama a API e só é ligado com chave.
- **Chave:** a chave vem de `ANTHROPIC_API_KEY` (variável de ambiente, user secret ou secret do GitHub), nunca de arquivo versionado. Na fase 9 ela vai para o cofre de segredos da nuvem escolhida.
- **Falhas:** tempo limite de 20 segundos. Erro do provedor, resposta truncada ou recusa viram estados explícitos (`unavailable`, `refused`) e a triagem segue com as regras.

Termos de dados: pela API comercial da Anthropic, o conteúdo enviado não é usado para treinar modelos e tem retenção limitada. Mesmo assim, só sai texto pseudonimizado. Para uso com pacientes reais seria preciso revisar contrato, DPA e localização do processamento (LGPD art. 33), e isso fica registrado como pendência fora do escopo da vitrine.

### Texto livre sai da clínica só mascarado (decisão de Cleber)

O texto mascarado vai ao modelo. Se o mascaramento cobrir mais da metade do texto, a queixa não sai (`skipped-over-masked`): o que sobraria não serve para triar e ainda exporia contexto. A queixa original fica só no banco do Triage, para o profissional que revisa.

### Consentimento próprio para a IA

`ai-triage` é uma finalidade separada no Consent (ADR-0008) e só aceita a base legal `specific-consent`. O Triage mantém uma projeção alimentada pelos eventos `ConsentGranted` e `ConsentRevoked`, com versão: duplicatas e eventos atrasados são ignorados, então uma concessão antiga nunca desfaz uma revogação.

Sem evento nenhum ainda, a projeção fica em `unknown` e conta como não consentido. A revogação vale para as próximas triagens. As anteriores continuam com a revisão do profissional, porque a sugestão já foi registrada.

### Auditoria com resumo do que saiu (decisão de Cleber: só o hash na trilha)

Toda sugestão, leitura, confirmação e correção entra na trilha HMAC do paciente (ADR-0007):

- **Ações:** `triage.suggested.model`, `.model-raised`, `.rules` ou `.none`; `triage.read`; `triage.confirmed`; `triage.corrected`.
- **Resumo do que saiu:** quando a queixa mascarada foi enviada ao modelo, a trilha recebe o SHA-256 do pedido exato (versão, instruções, schema e caso). O texto não vai.
- **Comprovação:** quem tem o texto, no banco do Triage, comprova que foi aquilo que saiu. O texto pode ser apagado a pedido do titular (fase 8) sem quebrar a cadeia.
- **Registro conservador:** o resumo é gravado sempre que um pedido foi montado para o provedor, mesmo que ele não tenha respondido. Uma chamada que estourou o tempo pode ter chegado ao provedor.

Para isso a auditoria ganhou um evento novo, `audit.content-disclosed`, com o resumo. Pela regra do repositório, um evento publicado não muda de forma, então `DataAccessRecorded` não ganhou campo.

O resumo entra no HMAC da entrada como um marcador `0x01` seguido dos 32 bytes, e só quando existe. Entradas antigas mantêm o mesmo hash, o que é provado por um vetor fixo nos testes. Trocar o resumo de uma entrada quebra a verificação da cadeia.

### Avaliação no CI sem pagar por build (decisão de Cleber: respostas gravadas)

- **Conjunto de avaliação:** `eval/triage/cases.json` tem 57 casos sintéticos: queixas comuns, sinais de alarme, ataques de injeção (incluindo três que escapam do detector de propósito) e queixas recheadas de dados pessoais.
- **Gravações:** cada resposta real do modelo vira um arquivo em `eval/triage/recordings`, cujo nome é o SHA-256 do pedido. Se instruções, schema, casos ou mascaramento mudarem, a gravação deixa de casar e precisa ser refeita.
- **Workflow de gravação:** o workflow manual `triage-recordings.yml` (Actions, ou o rótulo `gravar-triagem` num PR do próprio repositório) é o único lugar que chama o modelo. Ele usa o secret `ANTHROPIC_API_KEY`, um teto de chamadas por execução e publica as gravações como artefato para revisão humana antes do commit. O limite de gasto mensal fica configurado no console da Anthropic.
- **CI:** todo build reproduz as gravações sem rede e publica as métricas em `triage-evaluation.json`: acerto exato, acerto com uma prioridade de distância, subtriagem, alarmes nunca rebaixados, injeções barradas, recusas e marcadores por tipo.
- **Barreiras que sempre valem**, com ou sem gravação:
  - nenhum alarme rebaixado;
  - todo caso de alarme detectado;
  - toda injeção marcada como bloqueável barrada;
  - nenhum falso positivo de injeção;
  - nenhum dado pessoal dos casos no pedido;
  - nenhuma justificativa com linguagem clínica aceita.
- **Barreiras de qualidade:** acerto e taxa de recusa só são cobrados quando não falta nenhuma gravação. `recordingsRequired` passa a `true` assim que as primeiras gravações forem versionadas.

### Painel: a triagem gravada pelo CI

O Painel do ADR-0010 ganha o quinto cenário. `PanelRecordingTests` roda quatro triagens contra o sistema inteiro, com o Triage reproduzindo as gravações:

- queixa com nome e e-mail do paciente;
- sinal de alarme;
- tentativa de manipulação;
- queixa depois da revogação do `ai-triage`.

Cada triagem termina com a decisão do profissional. A gravação mostra o que o paciente escreveu, o que saiu para o modelo, a sugestão, as proteções acionadas, a revisão humana, as entradas de auditoria com o resumo e o trace.

As queixas vêm do conjunto de avaliação, então o pedido mascarado é idêntico ao gravado. Enquanto não houver gravação, o cenário mostra o modelo como indisponível e as regras sustentando a prioridade, sem simulação.

O Demo ganhou as rotas de triagem com as mesmas proteções da fase 6:

- só triagens da própria sessão podem ser revisadas (sem IDOR por id);
- no máximo 6 triagens por sessão;
- o envio espera a projeção do consentimento chegar, como a reserva espera a agenda;
- a limpeza da sessão apaga também os dados do Triage, menos a auditoria.

## Consequências

- **Garantias provadas por teste:** dado pessoal não chega ao provedor, aos logs, aos spans, às métricas nem à auditoria. Isso é verificado em integração, com queixas recheadas, e na varredura de telemetria de ponta a ponta, que agora cobre o Triage e procura também o texto da queixa. Alarme nunca é rebaixado. Injeção é barrada ou contida.
- **Custo:** zero por build; só as gravações manuais custam, com teto por execução e limite mensal no provedor.
- **Riscos residuais:**
  - **Mascaramento por regra não é perfeito.** Um nome que não é o do paciente, sem gatilho ("encontrei a Marta ontem"), ou um apelido podem passar. O teto de 50% e a ausência de identificadores no pedido reduzem o dano, não o eliminam.
  - **O detector de injeção é por padrão.** Paráfrases novas passam; por isso o piso dos alarmes, o schema e a revisão humana existem.
  - **Regras de alarme e casos de avaliação não são validados clinicamente.** Uso real exigiria protocolo validado (Manchester, por exemplo) e revisão médica.
  - **Uma única chave local de token de serviço.** O cofre aceita uma chave pública só, compartilhada por Notifications e Triage em desenvolvimento. Em produção cada serviço terá o seu par.
  - **Provedor fora do Brasil.** Uso real exige base de transferência internacional (LGPD art. 33) e DPA com o provedor.
- **Pendências para a fase 8:** apagamento a pedido do titular passa a incluir as triagens. O resumo na trilha sobrevive, por desenho.
