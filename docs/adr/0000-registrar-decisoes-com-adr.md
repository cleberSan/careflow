# ADR-0000: Registrar decisões arquiteturais com ADRs

- Status: aceito
- Data: 2026-10-14

## Contexto

O CareFlow é um projeto de portfólio construído em etapas públicas. Quem lê o repositório (clientes, recrutadores, colaboradores) precisa entender não só o que foi feito, mas por que, e quais alternativas foram descartadas.

## Decisão

Toda decisão com impacto em arquitetura, segurança, dados ou operação é registrada como ADR em `docs/adr/`, no formato `NNNN-titulo-em-kebab-case.md`, com as seções Contexto, Decisão, Alternativas e Consequências. ADRs não são editados depois de aceitos: uma mudança de rumo gera um novo ADR que substitui o anterior.

## Alternativas

- **Wiki do GitHub:** fica fora do versionamento junto com o código e não passa por revisão de PR.
- **Só descrições de PR:** o contexto se perde e não há um índice das decisões.

## Consequências

- Decisões passam por revisão no mesmo PR do código que as implementa.
- Custo pequeno de escrita a cada decisão relevante.
