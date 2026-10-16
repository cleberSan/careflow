# ADR-0007: Auditoria append-only com cadeia HMAC por clínica

- Status: aceito
- Data: 2026-10-15

## Contexto

A LGPD pede que o controlador consiga demonstrar quem tratou qual dado, e quando (art. 6º, X, e art. 37). Uma trilha de auditoria só serve como prova se não puder ser alterada sem deixar marca, inclusive por quem tem acesso ao banco.

## Decisão

- **Serviço próprio, `Audit`,** com banco próprio. Recebe eventos `audit.data-access` dos outros serviços pelo broker (ADR-0006) e só expõe leitura: `GET /v1/audit/entries` (por recurso, paginado por cursor) e `GET /v1/audit/verification`, ambos com o escopo `audit.read`.
- **Uma cadeia por clínica.** Cada entrada tem número de sequência e `hash(n) = HMAC-SHA256(chave, "careflow-audit-v1" ‖ campos(n) ‖ hash(n-1))`, com os campos codificados com prefixo de tamanho (não há como dois conjuntos de campos diferentes produzirem a mesma sequência de bytes). A primeira entrada encadeia num hash de zeros.
- **HMAC com chave fora do banco, em vez de SHA-256 puro.** Com hash sem chave, quem consegue escrever no banco recalcula a cadeia inteira depois de adulterar. Com HMAC, precisa também da chave, que fica em segredo de configuração (user-secrets em desenvolvimento, cofre de segredos em produção). Cada entrada guarda o id da chave, então a chave pode ser trocada sem quebrar a verificação das entradas antigas.
- **Cabeça da cadeia.** `audit_chain_heads` guarda a última sequência e o último hash de cada clínica. A gravação trava essa linha (`FOR UPDATE`), então gravações simultâneas da mesma clínica se enfileiram e a sequência não tem buracos. A verificação compara o fim da cadeia com a cabeça e detecta entradas apagadas do final.
- **Append-only no banco.** Um trigger recusa `UPDATE`, `DELETE` e `TRUNCATE` em `audit_entries` para qualquer papel, e a API só tem `SELECT` e `INSERT`. RLS por clínica nas duas tabelas.
- **Verificação.** Lê a cadeia da clínica em ordem, num snapshot `REPEATABLE READ`, e informa o tipo e a sequência da primeira quebra: buraco na sequência, elo quebrado, hash divergente, chave desconhecida ou cabeça divergente.
- **Idempotência.** Além do Inbox, um índice único `(clínica, message_id)` impede a mesma mensagem de entrar duas vezes na cadeia.
- **Quem e o quê.** `actor` é o `sub` do token (ou o nome do serviço, em ações automáticas, como o cancelamento por revogação), `resourceId` é um pseudônimo. A trilha não guarda dado pessoal. Leituras também são registradas, inclusive a do próprio consentimento e a do cadastro no cofre.

## Alternativas

- **Ledger gerenciado (Amazon QLDB, Azure SQL Ledger):** prova criptográfica pronta, mas o QLDB foi descontinuado e o Ledger prende o CareFlow a um banco e a uma nuvem ainda não escolhidos.
- **Assinatura assimétrica por entrada:** permite verificação por terceiros sem a chave secreta, ao custo de mais CPU por entrada e de um KMS com assinatura. Faz sentido junto com a ancoragem externa, abaixo.
- **Uma cadeia global:** um único ponto de contenção para todas as clínicas e uma verificação que mistura dados de clínicas diferentes.

## Consequências

- Quem tiver a chave HMAC e acesso de superusuário ao banco consegue reescrever a cadeia. A mitigação planejada é ancorar periodicamente o hash da cabeça fora do banco (armazenamento imutável ou serviço de carimbo de tempo), na fase 9.
- Um superusuário pode desligar triggers (`session_replication_role`). A adulteração continua detectável pela verificação, que é o que os testes provam: alterar um campo, apagar no meio, apagar no fim, trocar hashes entre entradas e usar uma chave desconhecida são todos detectados.
- A trilha cresce sem limite nesta fase. A retenção e o expurgo seguro (que precisa preservar a verificabilidade) entram com os direitos do titular, na fase 8.
  - **Atualização (fase 8, ADR-0012):** a eliminação a pedido do titular não tira nada da cadeia. A trilha só tem pseudônimos, fica sem prazo na política de retenção e os testes provam que ela continua íntegra depois da eliminação. Expurgo por prazo segue sem implementação.
- A gravação por clínica é serializada pela trava da cabeça. Suficiente para o volume da demonstração; se virar gargalo, a cadeia pode ser particionada por clínica e período.
