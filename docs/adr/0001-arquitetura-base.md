# ADR-0001: Arquitetura base e convenções do repositório

- Status: aceito
- Data: 2026-10-14

## Contexto

O CareFlow será uma plataforma multi-clínica de agendamento e triagem com privacidade por design (LGPD). O objetivo é demonstrar sistemas distribuídos, segurança e qualidade de engenharia, crescendo em fases sem reescritas.

## Decisão

- **Runtime:** .NET 10 (LTS), versão do SDK fixada em `global.json`.
- **Repositório:** monorepo com `src/Services/*` (um serviço por contexto delimitado), `src/BuildingBlocks` (código compartilhado mínimo), `tests/*` e `docs/adr`.
- **Estrutura interna dos serviços:** Vertical Slices, com modelo de domínio (DDD) nos contextos com regra rica (Agenda e Consentimento).
- **Erros de negócio:** Result Pattern (`Result`, `Result<T>`, `Error`) em vez de exceptions; exceptions ficam para erros de programação e falhas de infraestrutura.
- **Qualidade no build:** avisos tratados como erro, analisadores `latest-recommended`, estilo verificado no build e no CI (`dotnet format`).
- **Dependências:** versões centralizadas (`Directory.Packages.props`), lock files com restore travado no CI, Dependabot e revisão de dependências nos PRs.
- **Testes:** xUnit v3 sobre Microsoft.Testing.Platform, padrão AAA, cobertura via coverlet com mínimo de 80% de linhas no CI.
- **Segurança no CI:** CodeQL (`security-extended`) para C# e para os próprios workflows.

## Alternativas

- **Repositório por serviço:** isolamento maior, mas custo alto de coordenação para um projeto de uma pessoa nesta fase.
- **Clean Architecture em camadas por serviço:** mais cerimônia e projetos vazios para serviços pequenos; Vertical Slices mantém cada caso de uso coeso.
- **Exceptions para regra de negócio:** mais simples de escrever, porém caras, implícitas e difíceis de mapear para ProblemDetails.

## Consequências

- `BuildingBlocks` deve permanecer pequeno: só entra o que for usado por pelo menos dois serviços.
- Atualizar pacotes exige regenerar os lock files (`dotnet restore --force-evaluate`).
- Mensageria, banco e orquestração local (.NET Aspire) serão decididos em ADRs próprios quando entrarem no roadmap.
