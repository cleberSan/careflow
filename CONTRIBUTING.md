# Como contribuir

## Fluxo

1. Crie uma branch a partir de `main` (`feat/...`, `fix/...`, `chore/...`, `docs/...`).
2. Use [Conventional Commits](https://www.conventionalcommits.org/pt-br/) nas mensagens.
3. Abra um PR usando o template; o CI precisa estar verde.
4. Decisões arquiteturais entram como ADR em `docs/adr/`.

## Rodando localmente

Pré-requisitos: .NET SDK 10 (versão em `global.json`) e Docker (os testes de integração sobem um PostgreSQL com Testcontainers).

```bash
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release --coverlet --coverlet-output-format cobertura
```

Para usar um PostgreSQL já existente nos testes de integração, defina `CAREFLOW_TEST_POSTGRES` com a connection string.

### Banco local

```bash
docker compose up -d postgres
```

O script `deploy/postgres/init-dev.sql` cria o banco do Patient Vault e os logins das APIs (`scheduling_api`, `vault_api`), sem superusuário, para que o Row-Level Security valha também em desenvolvimento. As migrations rodam com o papel `careflow` (connection string `...Migrations`). Se o seu volume é anterior à fase 3, recrie-o uma vez: `docker compose down -v && docker compose up -d postgres`.

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

### Migrations

```bash
dotnet tool restore
dotnet ef migrations add NomeDaMigration --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --output-dir Infrastructure/Persistence/Migrations
```

Toda tabela nova com `tenant_id` precisa de `EnableTenantRowLevelSecurity` na migration (ADR-0003).

## Padrões

- Testes no padrão AAA (Arrange, Act, Assert), cobrindo cenários felizes, de erro e limites.
- Regras de negócio retornam `Result`; não use exceptions para fluxo.
- Nunca registre dados pessoais em logs, mensagens de erro ou testes. Use dados sintéticos (`SyntheticPatients` em `tests/CareFlow.Testing`).
