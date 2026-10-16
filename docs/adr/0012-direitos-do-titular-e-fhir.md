# ADR-0012: Direitos do titular de ponta a ponta e Bundle FHIR R4

- Status: aceito
- Data: 2026-10-16

## Contexto

A LGPD dá ao titular o direito de confirmar e acessar os próprios dados, de levá-los em formato estruturado (portabilidade) e de pedir a eliminação (art. 18). Até a fase 7, cada serviço guardava um pedaço do paciente e nenhum sabia responder por todos:

- o cofre (ADR-0004) tem o cadastro cifrado;
- a agenda, as consultas;
- o Consent, os consentimentos;
- o Notifications, os avisos;
- o Triage, as queixas e as decisões;
- a Auditoria (ADR-0007), a trilha, que é só de inserção e encadeada por HMAC.

A fase 8 precisa de um pedido que atravesse os sete serviços sem quebrar o que as fases anteriores garantiram: nenhum dado pessoal em evento, log ou telemetria, isolamento por clínica e cadeia de auditoria verificável. A eliminação tem um conflito de fundo: apagar o paciente e manter a trilha que prova o que foi feito com ele.

Este projeto é uma vitrine com dados 100% sintéticos. Prazos e bases legais da política abaixo são referência para a demonstração, não parecer jurídico.

## Decisão

### Serviço de pedidos

Um serviço novo, `CareFlow.Rights.Api`, no mesmo molde dos outros: banco próprio com RLS por clínica, logins `rights_api` e `rights_dispatcher` sem privilégio de dono, Outbox/Inbox (ADR-0006) e a mesma redação de telemetria (ADR-0010).

Rotas:

- `POST /v1/subject-requests` (`rights.write`): recebe `access`, `portability` ou `erasure` para um pseudônimo. Responde 202 e manda o código de confirmação. Um segundo pedido em aberto do mesmo tipo para o mesmo paciente responde 409.
- `GET /v1/subject-requests/{id}` (`rights.read`): situação, prazo, andamento por serviço, comprovante e pacote.
- `POST /v1/subject-requests/{id}/identity-confirmation` (`rights.write`): confere o código.
- `POST /v1/subject-requests/{id}/download-links` (`rights.write`): gera o link do pacote.
- `POST /v1/downloads` (anônima, 10 por minuto por IP): baixa o pacote com o token no corpo.
- `POST /v1/subject-requests/{id}/erasure-replay` (`rights.admin`): reaplica uma eliminação depois de restaurar um backup.

Estados: `received`, `in-progress`, `completed` e `denied` com motivo (`no-contact`, `code-not-delivered`, `identity-not-confirmed`, `subject-not-found`). O prazo é de 15 dias a partir do recebimento (art. 19, II), fica visível em toda resposta e o pedido vencido aparece como atrasado.

### Identidade antes de qualquer coisa sair (decisão de Cleber: código no contato do cadastro)

O titular prova que é o dono do contato cadastrado no cofre:

1. O serviço de pedidos gera um código de 6 dígitos, guarda só o HMAC dele e o manda cifrado (ECIES P-256) para a chave pública do Notifications no evento `IdentityVerificationRequested`. Outbox, broker e Inbox guardam mensagens por dias e não podem guardar um código válido em claro.
2. O Notifications abre o código só em memória, lê o contato no cofre e envia pela ordem de canais da clínica. O texto não vai para log, evento nem banco. Se o provedor aceitar e a resposta se perder, a mesma mensagem sai de novo com a mesma chave de idempotência e o provedor deduplica.
3. O código vale 10 minutos e aceita 5 tentativas. Código em formato inválido responde 400 sem gastar tentativa; código errado responde 400 com as tentativas restantes; vencido ou esgotado nega o pedido.

Sem contato no cadastro, o pedido é negado com `no-contact`. Um pseudônimo de outra clínica cai no mesmo caso: o cofre da clínica errada não o enxerga, então nenhum código sai e nada é apagado.

Na demonstração, o código vai para o provedor simulado e o Painel lê a mensagem numa caixa de entrada simulada (`GET /v1/demo/verification-messages/{id}` no Notifications), que só existe com `Demo:SimulatedInboxEnabled` e derruba a subida em produção.

### Seis serviços respondem pelo Outbox

Confirmada a identidade, o serviço de pedidos publica `SubjectRequestOpened` e cada serviço responde com `SubjectRequestAnswered`. O pedido só termina quando os seis responderam.

- **Exportação:** cada serviço monta a sua seção e a cifra para uma chave pública gerada só para aquele pedido. A chave privada fica embrulhada (AES-GCM) pela chave do serviço de pedidos. Os eventos levam só o pseudônimo e o cifrado.
- **Eliminação:** cada serviço aplica a sua parte e responde com as linhas do comprovante (item da política, ação, quantidade). O serviço de pedidos confere cada linha contra a política daquele serviço; uma resposta fora da política vai para a fila de mensagens mortas.

As respostas são processadas com trava por pedido (advisory lock), então seis respostas chegando juntas não montam o pacote duas vezes. Uma resposta repetida ou de um pedido já fechado é ignorada.

### Pacote do titular (decisão de Cleber: guardado 7 dias)

Com todas as seções, o serviço de pedidos abre as seções em memória e monta um ZIP com três arquivos:

- `leia-me.html`: legível, com tudo escapado e CSP `default-src 'none'`;
- `dados.json`: estruturado (`careflow.pacote-do-titular`, versão 1), uma seção por serviço;
- `fhir-bundle.json`: o Bundle FHIR R4 abaixo.

O pacote é cifrado com uma chave própria (embrulhada pela chave do serviço) e guardado por 7 dias. As seções cifradas são descartadas assim que o pacote existe. Na expiração, uma rotina a cada 10 minutos destrói a chave do pacote e o conteúdo.

O link vale 15 minutos e serve uma vez. O token é aleatório (32 bytes) com a clínica no começo, e o banco guarda só o SHA-256 dele. O download é anônimo, porque quem baixa é o titular: o token diz a clínica, a rota entra no escopo dela e qualquer falha (token inválido, vencido, usado, pacote destruído) responde o mesmo 404. A resposta sai com `Cache-Control: no-store`. Criação do link e download vão para a trilha.

### Eliminação com crypto-shredding

- **Cofre:** cada paciente ganha uma DEK própria, embrulhada pela chave da clínica (esquema 2). Eliminar destrói a linha da DEK: os campos cifrados ficam no banco, ilegíveis, inclusive em cópias e backups que não tenham a DEK. O blind index do CPF é apagado, então o CPF não acha mais o pseudônimo e pode ser cadastrado de novo. O pseudônimo e a data da eliminação ficam como prova. Ler o paciente depois disso responde 410.
- **Agenda:** cancela as consultas futuras com motivo próprio (sem aviso ao paciente), mantém as passadas pelo prazo da política e recusa novas reservas do pseudônimo.
- **Consent:** revoga o que é revogável, mantém o histórico (a prova do consentimento) e recusa novas concessões ao pseudônimo.
- **Notifications:** os avisos pendentes não saem, a referência ao provedor é apagada e o registro fica pelo prazo da política.
- **Triage:** apaga a queixa, o texto mascarado e a justificativa do modelo. A decisão e o resumo SHA-256 do que saiu (o mesmo da trilha, ADR-0011) ficam.
- **Auditoria:** nada sai da cadeia. A trilha só tem pseudônimos e ações, e a verificação continua íntegra depois da eliminação, como os testes provam.

### Política de retenção fixa e versionada (decisão de Cleber: regra fixa)

`RetentionPolicy` (em `CareFlow.Contracts`) é igual para todas as clínicas e tem versão (`2026-10.1`). Cada comprovante de eliminação leva a versão, e cada linha diz o item, o serviço, a ação (`destroyed`, `erased`, `cancelled`, `retained`), a quantidade, a base e até quando fica. Mudar um prazo é uma versão nova, nunca uma edição da atual.

Prazos de referência: 5 anos para consultas passadas, histórico de consentimento e registro de avisos (defesa em processo e prova do consentimento); 20 anos para a decisão da triagem, se a clínica a tratar como prontuário; a trilha de auditoria fica sem prazo, porque a cadeia precisa continuar verificável.

### Restauração de backup

Restaurar um backup do cofre de antes da eliminação traz a DEK de volta. Para isso existe `erasure-replay`: depois de uma restauração, um administrador reaplica as eliminações concluídas e cada serviço reaplica a sua parte, de forma idempotente. Num banco que não foi restaurado, nada muda. O teste de ponta a ponta restaura as linhas do paciente no cofre, mostra o cofre lendo de novo e depois ilegível após a reaplicação, com a cadeia íntegra.

### Migração e rotação de chaves do cofre sem parar

- O cofre lê os dois esquemas (campos com a chave da clínica e campos com a DEK do paciente) e todas as versões da chave da clínica.
- `POST /v1/keys/maintenance` (`vault.keys`) leva um lote de registros para o esquema e a versão atuais. Repete-se até restar zero. Cada registro é gravado com concorrência otimista; um paciente alterado no meio fica para o próximo lote.
- `POST /v1/keys/rotation` cria a próxima versão da chave da clínica. `DELETE /v1/keys/{versão}` destrói uma versão antiga quando nenhum registro depende mais dela.
- Eliminar um paciente ainda no esquema antigo sobrescreve os campos com bytes aleatórios, porque a chave da clínica continua viva.

Num deploy em rolling, as instâncias antigas não leem o esquema 2. Por isso a ordem é: publicar a versão nova em todas as instâncias e só depois rodar a manutenção.

### Bundle FHIR R4 (decisão de Cleber: Firely SDK)

O pacote leva um Bundle `collection` só de leitura, com referências `urn:uuid`:

- `Patient` com o CPF no sistema `http://rnds.saude.gov.br/fhir/r4/NamingSystem/cpf`, nome, nascimento e contatos;
- `Appointment` por consulta, com situação e motivo de cancelamento;
- `Consent` por finalidade, com escopo `patient-privacy`, categoria LOINC 59284-0 e a base legal em `policyRule`.

Usamos o Firely SDK (`Hl7.Fhir.R4` 6.7.0, BSD-3-Clause) para montar e serializar. Os testes validam o Bundle contra os perfis base do R4 com `Firely.Fhir.Validation.R4` 3.5.0 e o pacote de especificação. A validação roda só nos testes, então o pacote de especificação não vai para a imagem. As licenças estão em `THIRD-PARTY-NOTICES.md`.

### Painel

O Painel ganha o sexto cenário, gravado pelo CI em `PanelRecordingTests` (schema 3):

- portabilidade: o código chega na caixa de entrada simulada, o pedido passa pelos seis serviços e o pacote é baixado pelo link; o mesmo link é recusado na segunda vez;
- eliminação: comprovante com a versão da política, o cofre respondendo 410 e a trilha com a cadeia íntegra;
- o trace da eliminação atravessando os oito serviços.

O Demo só aceita pedidos da própria sessão (sem IDOR por id), no máximo 4 por sessão, sempre para o paciente sintético dela. O ZIP não sai do Demo: o Painel mostra só os nomes dos arquivos, as seções e os recursos FHIR. A limpeza da sessão apaga também os pedidos e pacotes, menos a auditoria.

## Alternativas

- **Apagar as linhas do cofre em vez de destruir a chave:** não alcança backups nem réplicas. Crypto-shredding torna ilegível toda cópia do cifrado de uma vez.
- **Tirar o paciente da trilha de auditoria:** quebraria a cadeia HMAC e apagaria justamente a prova do que foi feito. A trilha já guarda só pseudônimos.
- **Pacote gerado a cada download:** exigiria manter as seções abertas ou pedir de novo aos seis serviços. Um pacote cifrado por 7 dias, com chave destruída na expiração, é mais simples de auditar.
- **Token do link na URL:** ficaria em histórico, logs de proxy e cabeçalho Referer. O token vai no corpo de um POST.
- **Confirmar identidade por login do paciente:** o CareFlow não tem conta de paciente. O código no contato já cadastrado não cria nova superfície.
- **Montar o JSON FHIR à mão:** sem validação, um Bundle quebrado passaria em silêncio. O Firely é a referência em .NET e tem licença permissiva.

## Consequências

- **Garantias provadas por teste:**
  - portabilidade com seções dos seis serviços, Bundle válido e link de uso único;
  - eliminação deixando o cofre ilegível com a cadeia íntegra, e reaplicada depois de uma restauração;
  - pedido de outra clínica negado sem tocar a clínica dona;
  - varredura de telemetria cobrindo código, token e texto da mensagem;
  - limpeza do Demo incluindo pedidos e pacotes.
- **Riscos residuais:**
  - **Chave do serviço de pedidos estática.** `Rights:Keys:KeyEncryptionKey` vem de configuração (em produção, de um cofre de segredos) e ainda não tem rotação. Trocar a chave torna ilegíveis os pacotes guardados, o que é aceitável porque eles vivem 7 dias.
  - **Código por SMS ou WhatsApp.** Quem controla o chip ou a conta do paciente controla o pedido. Uso real pode exigir um segundo fator para eliminação.
  - **Prazos de retenção não são cumpridos sozinhos.** A política diz até quando cada item fica, mas não há rotina que apague o que venceu. Fica para uma fase futura.
  - **Restauração de backup depende de alguém lembrar do `erasure-replay`.** O runbook de restauração precisa incluir esse passo.
  - **Rolling deploy com o esquema 2.** A manutenção de chaves só pode rodar depois que todas as instâncias estiverem na versão nova.
