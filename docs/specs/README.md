# Specs

Especificações das mudanças do CareFlow (Spec-Driven Development): o que cada mudança deve fazer e por quê, escrito e revisado antes do código ([ADR-0015](../adr/0015-especificar-antes-de-implementar.md)). Decisões de arquitetura continuam nos [ADRs](../adr); a spec cita os que a restringem.

## Quando

| Nível | Quando | Artefatos |
|---|---|---|
| Direto | Atualização de dependência (ADR-0014), documentação, refatoração sem mudança de comportamento, bug com causa clara (o teste de regressão é a spec) | Só o PR |
| Leve | Comportamento novo ou alterado, pequeno, sem nenhum gatilho do completo | `spec.md` com plano e tarefas curtos |
| Completo | Qualquer gatilho abaixo | `spec.md`, `plano.md`, `tarefas.md`; código só depois de a spec ser aprovada |

Gatilhos do nível completo neste repositório:

- fase do roadmap;
- rota, escopo, evento de integração (tipo novo é `.vN`), schema ou migration, novos ou alterados;
- dado pessoal, cofre, consentimento, auditoria, RLS, retenção ou direitos do titular;
- instruções, schema ou mascaramento da triagem (exige regravar as respostas, ADR-0011);
- mudança que também mexe no portfolio-web (a spec mora aqui e o PR do site linka);
- pacote, serviço externo, custo ou licença novos.

## Como

1. Pasta `NNNN-titulo-em-kebab-case/` com o próximo número livre, nunca reaproveitado.
2. `spec.md`: problema, fora de escopo, critérios de aceite `CA-NN` em EARS (`QUANDO ..., ... DEVE ...`), requisitos não funcionais com meta numérica e perguntas abertas.
3. `plano.md`: contexto, fatos com fonte e data, fatias verticais, contratos e dados, segurança, observabilidade, teste por critério, riscos e rollback.
4. `tarefas.md`: uma tarefa por commit, cada uma com a prova e os critérios que cobre.
5. Cada teste que prova um critério leva o comentário `// spec NNNN CA-NN` logo acima do `[Fact]` ou `[Theory]`. Conferência: `grep -rn "spec NNNN CA-" tests/`.
6. O PR linka a spec e mostra a tabela critério → teste. Ao concluir, o status vira `implementada`; depois disso a spec não é reescrita, e comportamento novo ganha spec nova.

Specs são públicas: só dados sintéticos, nada de segredo, conta de nuvem ou contato. Modelos e processo completo: [claude-works](https://github.com/cleberSan/claude-works/blob/main/memorias/spec-driven-development.md).
