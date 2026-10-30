# ADR-0014: Atualização de dependências agrupada e mensal, ainda sem auto-merge

- Status: aceito
- Data: 2026-10-30

## Contexto

O Dependabot abria um PR por pacote NuGet toda semana. Em 30/10 eram cinco abertos desde 21/10, e quatro estavam vermelhos no `Build, formatação e testes` com o mesmo erro no restore travado:

```
error NU1004: The project references careflow.rights.api whose dependencies has changed.
The packages lock file is inconsistent with the project dependencies so restore can't be run in locked mode.
```

O Dependabot muda o `Directory.Packages.props` e o `packages.lock.json` de quem referencia o pacote direto, mas não o dos projetos que recebem o pacote por referência a outro projeto (`CentralTransitive` no lock file). Como o CI restaura em modo travado (ADR-0001), o PR falha. Só passa a atualização de pacote usado direto por um projeto folha, como o `Hl7.Fhir.Specification.Data.R4`, que só os testes do Rights usam.

O Cleber pediu que o bot não precise dele a cada atualização, com critério para nada sair do ar. Nos sites isso virou auto-merge criterioso (portfolio-web ADR-0007, lojinha-da-lua ADR-0010). Aqui, três coisas impedem o mesmo agora:

1. **Lock files:** quase todo PR de NuGet chega vermelho, então o auto-merge quase nunca dispararia.
2. **Imagens por commit:** o merge feito com o `GITHUB_TOKEN` não dispara workflows de `push`. O `Imagens` não publicaria as imagens desse commit, e o deploy (ADR-0013), que por padrão leva o último commit da `main`, pararia em "Conferir as oito imagens no GHCR". Isso quebra a regra de que qualquer commit da `main` pode ir ao ar.
3. **Branch atualizado não é exigido** no ruleset da `main`: o PR pode passar nos checks contra uma `main` antiga e ser mesclado sem testar o resultado.

## Decisão

- **Mensal, com cooldown de 7 dias:** NuGet e GitHub Actions rodam no dia 1º às 5h. Versão nova só vira PR depois de uma semana publicada. Correção de segurança não espera nenhum dos dois.
- **Grupos de pacotes que precisam casar versão:** OpenTelemetry, FHIR (Firely), testes (xUnit, Coverlet, Testcontainers, `Mvc.Testing`) e um grupo `dotnet` com minor e patch de `Microsoft.*` e `Npgsql*`, que acompanham o patch mensal do .NET 10.
- **Pacotes sensíveis continuam em PR próprio:** SDK da Anthropic (triagem), SDK da Oracle (chave mestra no OCI Vault), `RabbitMQ.Client` (mensageria) e `Newtonsoft.Json` (fixado por segurança). O CI não exercita nenhum deles contra o serviço real.
- **Sem auto-merge** até existir um jeito de atualizar os lock files e de o merge disparar os workflows de `push`.

## Como tratar um PR do Dependabot aqui

1. Verde: revisar e mesclar. Major de SDK da Anthropic ou da Oracle pede leitura do changelog antes.
2. Vermelho com NU1004: no branch do PR, rodar `dotnet restore --force-evaluate`, conferir que só mudaram arquivos `packages.lock.json` e fazer push. O push de uma pessoa dispara o CI de novo. Depois disso o Dependabot não faz mais rebase do PR; se precisar, `@dependabot recreate` refaz do zero.
3. Também vale abrir um PR único que aplica o grupo do mês e regenera os lock files, e fechar os do Dependabot.

## Caminho para automatizar

Um GitHub App gratuito do próprio Cleber, instalado só neste repositório, com escrita em conteúdo e PRs:

- Um job sem a chave roda `dotnet restore --force-evaluate` no PR e guarda os lock files como artefato. Outro job, com o token do App, confere que só lock files mudaram e faz o commit no branch. Commit feito com token de App dispara o CI.
- O auto-merge ligado com o token do App faz o merge disparar `Imagens`, CI e CodeQL na `main`.
- Exigir branch atualizado no ruleset da `main`.

O custo é uma chave privada com escrita guardada nos secrets de Actions e do Dependabot, que precisa de rotação, e umas 80 linhas a mais de workflow para manter. Fica para decisão do Cleber.

## Alternativas

- **Tirar o restore travado:** resolveria o NU1004, mas abriria mão da garantia de que o CI restaura exatamente os pacotes revisados (hash por pacote). Não.
- **Auto-merge só das actions:** não tem lock file, mas cai no problema das imagens por commit.
- **Token pessoal (PAT) no lugar do App:** mesma automação, com uma credencial presa à conta do Cleber e escopo maior. Pior que o App.

## Consequências

- Em vez de um PR por pacote por semana, chegam por mês um PR por grupo e um por pacote sensível.
- Os PRs de NuGet que tocam pacotes compartilhados continuam precisando de um push com os lock files regenerados.
- Fora deste ADR: as imagens do Caddy, PostgreSQL e RabbitMQ em `deploy/prod/compose.yaml` têm versão fixa e o Dependabot não olha para elas. Vale incluir o ecossistema `docker-compose` quando a demonstração estiver no ar.
