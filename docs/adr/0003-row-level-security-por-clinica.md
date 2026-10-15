# ADR-0003: Row-Level Security por clínica no PostgreSQL

- Status: aceito
- Data: 2026-10-14

## Contexto

Desde a fase 2 a clínica (tenant) vem da claim `tenant_id` do token e um filtro global do EF Core restringe as consultas a ela. Isso protege contra IDOR enquanto todo acesso passar pelo EF e nenhum código desligar o filtro (`IgnoreQueryFilters`, SQL cru, um novo `DbContext` mal configurado). Com o Patient Vault guardando dados pessoais e de saúde (LGPD art. 11), uma única camada no código da aplicação não basta: um bug não pode virar vazamento entre clínicas.

## Decisão

- **RLS em todas as tabelas com dados de clínica** (agenda e cofre), com `ENABLE` e `FORCE ROW LEVEL SECURITY`. A política `tenant_isolation` compara `tenant_id` com `current_setting('app.tenant_id')` tanto na leitura (`USING`) quanto na escrita (`WITH CHECK`). Sem clínica na sessão, o valor é vazio e nenhuma linha é visível (falha fechada).
- **Clínica gravada na sessão pelo interceptor do EF.** `TenantSessionInterceptor` executa `set_config('app.tenant_id', @tenant, false)` a cada abertura de conexão, sempre sobrescrevendo o valor (vazio quando não há clínica). Valor de sessão, e não de transação, porque o EF executa leituras fora de transação explícita; o reset do pool do Npgsql e a sobrescrita na abertura impedem que uma conexão reaproveitada carregue a clínica de outra requisição.
- **Separação de papéis.** As migrations rodam com o papel dono do schema (`ConnectionStrings:{Serviço}Migrations`, só em desenvolvimento, testes e no pipeline de deploy). A API conecta com um login sem superusuário, sem `BYPASSRLS` e sem ser dono das tabelas, membro de um papel por serviço (`scheduling_app`, `vault_app`) que só recebe os comandos necessários: agenda e pacientes sem `DELETE`, chaves de clínica só `SELECT` e `INSERT`.
- **Readiness recusa conexão privilegiada.** O health check `row-level-security` fica `Unhealthy` se o papel da API for superusuário ou tiver `BYPASSRLS`, então uma connection string de admin em produção não recebe tráfego.
- **Duas camadas independentes.** O filtro do EF continua: dá 404 limpo, usa os índices por clínica e protege mesmo que o RLS seja desligado por engano numa migration futura. O RLS protege mesmo que o filtro falhe.
- **Código compartilhado.** Tenancy, autorização por escopo, ProblemDetails e observabilidade foram para `CareFlow.BuildingBlocks.AspNetCore`; RLS, interceptor e health check para `CareFlow.BuildingBlocks.Persistence`. Agora há dois serviços usando o mesmo código (regra do ADR-0001).

## Alternativas

- **Banco ou schema por clínica:** isolamento físico mais forte, mas migrations, conexões e custo crescem com o número de clínicas. Fica como opção para clientes que exigirem isolamento físico.
- **`SET LOCAL` em transação explícita por requisição:** dispensa reset de sessão, mas obriga toda leitura a abrir transação e muda o modelo de concorrência da agenda.
- **Connection string por clínica (`Options=-c app.tenant_id=...`):** um pool por clínica, o que esgota conexões com muitas clínicas.
- **Só filtro do EF (como na fase 2):** não protege contra SQL cru, `IgnoreQueryFilters` ou um segundo `DbContext`.

## Consequências

- Os testes de integração rodam como em produção: migrations com admin e API com login sem privilégios. Há testes direto no banco (sem a API) provando que sem clínica nada aparece, que outra clínica não é lida nem alterada, que inserir ou mover linha para outra clínica é recusado e que o login da API não apaga nem desliga o RLS.
- Cada abertura de conexão custa uma ida extra ao banco (`set_config`). Aceitável neste volume; se virar gargalo, a alternativa é manter a conexão aberta durante a requisição.
- RLS não protege contra SQL injection que execute `set_config` com outra clínica. A defesa contra injection continua sendo a parametrização do EF e do Npgsql.
- Papéis de banco são do cluster, não do banco. As migrations criam o papel da aplicação sem login; o login com senha é criado pela infraestrutura (script local em `deploy/postgres`, IaC na fase 9), com a senha fora do repositório.
- Em desenvolvimento, quem já tinha o volume do Postgres da fase 2 precisa recriá-lo (`docker compose down -v`) para o script de logins rodar.
