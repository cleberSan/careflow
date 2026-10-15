# Como contribuir

## Fluxo

1. Crie uma branch a partir de `main` (`feat/...`, `fix/...`, `chore/...`, `docs/...`).
2. Use [Conventional Commits](https://www.conventionalcommits.org/pt-br/) nas mensagens.
3. Abra um PR usando o template; o CI precisa estar verde.
4. Decisões arquiteturais entram como ADR em `docs/adr/`.

## Rodando localmente

Pré-requisitos: .NET SDK 10 (versão em `global.json`) e Docker (os testes de integração sobem PostgreSQL e, nos testes de ponta a ponta, RabbitMQ com Testcontainers).

```bash
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release --coverlet --coverlet-output-format cobertura
```

Para usar um PostgreSQL já existente nos testes de integração, defina `CAREFLOW_TEST_POSTGRES` com a connection string.

`tests/CareFlow.EndToEndTests` sobe os quatro serviços num processo, com PostgreSQL e RabbitMQ reais, e cobre a propagação da revogação, broker fora do ar, duplicatas, desordem e mensagens mortas.

### Banco e broker locais

```bash
docker compose up -d
```

O script `deploy/postgres/init-dev.sql` cria um banco por serviço, os logins das APIs (`*_api`) e dos despachantes de mensagens (`*_dispatcher`), todos sem superusuário, para que o Row-Level Security valha também em desenvolvimento. As migrations rodam com o papel `careflow` (connection string `...Migrations`). Se o seu volume é anterior à fase 4, recrie-o uma vez: `docker compose down -v && docker compose up -d`.

O RabbitMQ local fica em `localhost:5672` (painel em `http://localhost:15672`, usuário `careflow`, senha `careflow-dev`, só para desenvolvimento). Com o broker fora, os serviços continuam atendendo e os eventos esperam no Outbox (ADR-0006).

### Subindo a API de agenda

```bash
dotnet run --project src/Services/Scheduling/CareFlow.Scheduling.Api
```

Em desenvolvimento a API aplica as migrations na subida. Para chamar os endpoints, gere um token local (a chave fica nos user secrets, fora do repositório):

```bash
dotnet user-jwts create --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --scope scheduling.write --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Há requisições de exemplo em `CareFlow.Scheduling.Api.http`.

### Subindo o Patient Vault

O cofre precisa de uma chave mestra local (KEK) de 32 bytes, guardada nos user secrets, fora do repositório:

```bash
dotnet user-secrets set "KeyManagement:Local:Keys:dev-1" "$(openssl rand -base64 32)" \
  --project src/Services/PatientVault/CareFlow.PatientVault.Api
dotnet run --project src/Services/PatientVault/CareFlow.PatientVault.Api
dotnet user-jwts create --project src/Services/PatientVault/CareFlow.PatientVault.Api \
  --scope "vault.read vault.write" --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Perder essa chave torna ilegíveis os pacientes cadastrados localmente. O provedor local é recusado em `Production`. Há requisições de exemplo, só com dados sintéticos, em `CareFlow.PatientVault.Api.http`.

### Subindo Consent e Audit

```bash
dotnet run --project src/Services/Consent/CareFlow.Consent.Api
dotnet user-jwts create --project src/Services/Consent/CareFlow.Consent.Api \
  --scope "consent.read consent.write" --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token

# A auditoria assina a cadeia com uma chave HMAC de 32 bytes, guardada fora do banco e do repositório.
dotnet user-secrets set "Audit:Chain:Keys:dev-1" "$(openssl rand -base64 32)" \
  --project src/Services/Audit/CareFlow.Audit.Api
dotnet run --project src/Services/Audit/CareFlow.Audit.Api
dotnet user-jwts create --project src/Services/Audit/CareFlow.Audit.Api \
  --scope audit.read --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Perder a chave da auditoria impede verificar as entradas assinadas com ela (ADR-0007).

### Migrations

```bash
dotnet tool restore
dotnet ef migrations add NomeDaMigration --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --output-dir Infrastructure/Persistence/Migrations
```

Toda tabela nova com `tenant_id` precisa de `EnableTenantRowLevelSecurity` na migration (ADR-0003). Outbox e Inbox usam `EnableOutboxSecurity` e `EnableInboxSecurity`, que também liberam o papel do despachante (ADR-0006).

### Eventos

- Publique pelo Outbox (`db.Enqueue(...)` ou `db.RecordAccess(...)`) antes do `SaveChanges`, nunca direto no broker: o evento e a mudança gravam juntos ou não gravam.
- Consumidores implementam `IIntegrationEventConsumer<TEvent>` e precisam tolerar desordem; duplicatas o Inbox já descarta.
- Eventos carregam só pseudônimos. O tipo do evento não muda depois de publicado: crie um tipo novo (`.v2`).

## Padrões

- Testes no padrão AAA (Arrange, Act, Assert), cobrindo cenários felizes, de erro e limites.
- Regras de negócio retornam `Result`; não use exceptions para fluxo.
- Nunca registre dados pessoais em logs, mensagens de erro ou testes. Use dados sintéticos (`SyntheticPatients` em `tests/CareFlow.Testing`).
