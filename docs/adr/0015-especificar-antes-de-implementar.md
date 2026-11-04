# ADR-0015: Especificar antes de implementar (Spec-Driven Development)

- Status: proposto
- Data: 2026-11-04

## Contexto

O CareFlow é construído em fases públicas. Até a fase 9, o escopo de cada fase vinha de um prompt escrito fora do repositório, as decisões viravam ADR e o comportamento esperado aparecia só nos testes e na descrição do PR. Quem lê o repositório acha por que uma arquitetura foi escolhida, mas não o requisito que cada teste prova nem o que ficou de fora de propósito, e a revisão do comportamento só acontecia com o código pronto.

Em 2026-11-04 Cleber decidiu adotar Spec-Driven Development em todos os repositórios do ecossistema. O processo comum e as ferramentas avaliadas (GitHub Spec Kit, OpenSpec, Kiro) estão no [ADR-0002 da base claude-works](https://github.com/cleberSan/claude-works/blob/main/docs/adr/0002-spec-driven-development.md).

## Decisão

- **Specs em `docs/specs/NNNN-titulo/`**, ao lado dos ADRs: `spec.md` (o quê e por quê), `plano.md` (como) e `tarefas.md` (em que ordem), conforme [docs/specs/README.md](../specs/README.md).
- **Nível completo, com a spec aprovada por Cleber antes do código,** quando a mudança é uma fase do roadmap; cria ou altera rota, escopo, evento de integração, schema ou migration; toca dado pessoal, cofre, consentimento, auditoria, RLS, retenção ou direitos do titular; muda instruções, schema ou mascaramento da triagem (ADR-0011); envolve o portfolio-web; ou traz pacote, serviço, custo ou licença novos.
- **Nível leve** (só `spec.md`) para mudança pequena de comportamento sem esses gatilhos; **direto** (só o PR) para atualização de dependência (ADR-0014), documentação, refatoração e bug com causa clara, em que o teste de regressão é a spec.
- **Critérios de aceite `CA-NN`** em EARS; o teste xUnit que prova cada um leva o comentário `// spec NNNN CA-NN` logo acima do `[Fact]` ou `[Theory]`.
- **Spec por mudança:** depois de `implementada`, só o status muda; comportamento novo ganha spec nova. ADRs continuam imutáveis, e a spec cita os que a restringem.
- **Template de PR** com a linha "Spec" e os itens de critério coberto.
- Nenhuma ferramenta nem pacote novo: os modelos vêm da base claude-works.

## Alternativas

- **Continuar só com ADR e prompt de fase:** o requisito segue fora do repositório e o comportamento só é revisado depois do código.
- **GitHub Spec Kit no repositório:** licença MIT, mas traz um CLI em Python e arquivos próprios para manter (ADR-0002 da base).
- **Spec viva por serviço, no estilo OpenSpec:** descreveria o estado atual de cada serviço, mas exigiria atualizá-la a cada mudança; o README, a tabela de rotas e os testes já cumprem esse papel.

## Consequências

- A fase 10 (testes de carga) é a primeira com spec completa.
- As fases 0 a 9 não ganham spec retroativa: os ADRs e os testes são o registro delas.
- Custo de escrita a cada mudança com spec; em troca, a revisão do PR confere critério sem teste e código fora da spec.
- Specs são públicas: só dados sintéticos, nada de segredo, conta de nuvem ou contato.
- Este ADR reabre junto com o ADR-0002 da base.
