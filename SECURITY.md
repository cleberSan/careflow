# Política de segurança

## Reportar uma vulnerabilidade

Não abra issue pública para vulnerabilidades. Use o [reporte privado de vulnerabilidades do GitHub](https://github.com/cleberSan/careflow/security/advisories/new). O retorno inicial acontece em até 5 dias úteis.

## Escopo

O CareFlow é um projeto de demonstração e trabalha **somente com dados sintéticos**. Não envie dados reais de pacientes em issues, PRs ou ambientes de demonstração.

## Práticas adotadas

- Segredos nunca são versionados; em nuvem, ficam em cofre de segredos (Azure Key Vault ou AWS Secrets Manager).
- Análise estática com CodeQL e revisão de dependências em todo PR.
- Atualizações de dependências mensais e agrupadas via Dependabot, com cooldown de 7 dias; correções de segurança chegam na hora ([ADR-0014](docs/adr/0014-atualizacao-de-dependencias-sem-auto-merge.md)).
