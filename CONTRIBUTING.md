# Como contribuir

## Fluxo

1. Crie uma branch a partir de `main` (`feat/...`, `fix/...`, `chore/...`, `docs/...`).
2. Use [Conventional Commits](https://www.conventionalcommits.org/pt-br/) nas mensagens.
3. Abra um PR usando o template; o CI precisa estar verde.
4. Decisões arquiteturais entram como ADR em `docs/adr/`.

## Rodando localmente

Pré-requisito: .NET SDK 10 (versão em `global.json`).

```bash
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release --coverlet --coverlet-output-format cobertura
```

## Padrões

- Testes no padrão AAA (Arrange, Act, Assert), cobrindo cenários felizes, de erro e limites.
- Regras de negócio retornam `Result`; não use exceptions para fluxo.
- Nunca registre dados pessoais em logs, mensagens de erro ou testes. Use dados sintéticos.
