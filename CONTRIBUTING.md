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

### Subindo a API de agenda

```bash
docker compose up -d postgres
dotnet run --project src/Services/Scheduling/CareFlow.Scheduling.Api
```

Em desenvolvimento a API aplica as migrations na subida. Para chamar os endpoints, gere um token local (a chave fica nos user secrets, fora do repositório):

```bash
dotnet user-jwts create --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --scope scheduling.write --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Há requisições de exemplo em `CareFlow.Scheduling.Api.http`.

### Migrations

```bash
dotnet tool restore
dotnet ef migrations add NomeDaMigration --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --output-dir Infrastructure/Persistence/Migrations
```

## Padrões

- Testes no padrão AAA (Arrange, Act, Assert), cobrindo cenários felizes, de erro e limites.
- Regras de negócio retornam `Result`; não use exceptions para fluxo.
- Nunca registre dados pessoais em logs, mensagens de erro ou testes. Use dados sintéticos.
