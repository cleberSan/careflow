# ADR-0013: Demonstração hospedada numa VM Always Free da Oracle

- Status: aceito
- Data: 2026-10-16
- Substitui: a parte de hospedagem do ADR-0010 (o Painel deixa de ser só replay)

## Contexto

Até a fase 8 o Painel de Privacidade e Resiliência só reproduzia gravações do CI (ADR-0010) e o CareFlow rodava apenas na máquina de quem desenvolve. A fase 9 publica o backend para o Painel ter um modo ao vivo. A restrição de Cleber: hospedagem gratuita e segura, porque o projeto é vitrine e custo recorrente de AWS ou Azure é um problema hoje.

A Vercel, onde o site já está, não serve: containers dormem depois de poucos minutos sem uso, não há Postgres nem RabbitMQ, e o plano Hobby limita horas de CPU. O Always Free da Oracle oferece uma VM Ampere A1 (arm64) com 2 OCPU, 12 GB de RAM e 200 GB de disco, mais Vault (chaves por software) e Object Storage dentro da cota gratuita.

Os dados continuam 100% sintéticos. Nada aqui é promessa clínica nem jurídica.

## Decisão

### Uma VM, o mesmo compose

A VM roda a stack inteira com `deploy/prod/compose.yaml`: Caddy, Postgres, RabbitMQ, os oito serviços, migrations e a observabilidade enxuta (Collector, Tempo, Loki, Prometheus e Grafana, retenção de 3 dias, decisão de Cleber: "tudo na VM"). Cada container tem limite de memória (somam menos de 8 GB), `restart: unless-stopped`, sistema de arquivos só leitura, `cap_drop: ALL` e `no-new-privileges`. O site continua na Vercel.

- **Ambiente `Demo`:** novo, ao lado de `Development` e `Production`. Conta como exposto à internet (`IsInternetFacing`): o provedor local de chaves e o modelo real da triagem sem trava explícita são recusados na subida. Os recursos de demonstração (limpeza de clínicas, caixa simulada, emissor HS256) continuam recusados só em `Production`.
- **Imagens:** uma por serviço, num pacote só do GHCR (`ghcr.io/clebersan/careflow:<serviço>-<commit>`), para Cleber tornar público um único pacote. O SDK compila na arquitetura do runner e só o runtime é arm64 (publicação framework-dependent, sem RID), então não há emulação. Runtime `aspnet:10.0-noble-chiseled-extra`: sem shell, usuário sem privilégio, com ICU e tzdata. Toda a main gera imagens, para qualquer commit da main poder ir para o ar.
- **Saúde sem curl:** o próprio binário responde `--health-probe http://127.0.0.1:8080/health/ready` (só loopback) e sai com 0 ou 1.
- **Migrations como passo do deploy:** um container de uma execução por serviço (`Database:MigrateAndExit`). A API só sobe depois dele.
- **Postgres com dono por banco:** cada serviço tem um dono sem superusuário (`<serviço>_owner`), que roda as migrations e a limpeza do Painel, sujeito ao FORCE RLS. O superusuário fica só para backup e restauração. As APIs e despachantes seguem com os papéis do ADR-0003.

### Entrada segura

- **HTTPS só na 443.** Caddy pede o certificado ao Let's Encrypt pelo desafio TLS-ALPN-01 na própria 443, então a porta 80 fica fechada. Sem HTTP/3. Domínio grátis no DuckDNS (decisão de Cleber), atualizado por um timer na VM.
- **Só `/demo/v1/*` passa.** Saúde, métricas e as APIs dos serviços não existem por fora (404). Corpo limitado a 8 KB no Caddy e 4 KB no Demo. Cabeçalhos HSTS, `nosniff`, `no-referrer`, CSP `default-src 'none'`, sem `Server`. Log do Caddy sem IP, cabeçalhos nem query.
- **Firewall em duas camadas.** Na lista de segurança da VCN só entram 22 e 443. No sistema, a imagem da Oracle recusa tudo menos a 22 e o cloud-init abre só a 443. Banco, broker e Grafana não publicam porta; o Grafana fica no loopback da VM, por túnel SSH.
- **SSH só por chave**, sem root, `AllowUsers ubuntu deploy`. Atualizações de segurança automáticas com reinício de madrugada.
- **Redes do compose:** `edge` (Caddy e Demo), `backend` interna sem saída e `egress` só para o cofre falar com o OCI Vault.

### Sessão do Painel por token, não cookie

O site (`vercel.app`) e o backend (`duckdns.org`) são sites diferentes, e navegadores bloqueiam cookie de terceiro. A sessão vira um token opaco de 32 bytes no `Authorization: Bearer`, guardado só na memória da página; o servidor guarda o hash. Como nada vai sozinho numa requisição de outro site, não há CSRF; o cabeçalho `X-CareFlow-Demo` e a lista de origens do CORS (só a do site, sem credenciais) seguem como segunda barreira.

Um rewrite na Vercel para manter a mesma origem foi descartado: o Demo veria só os IPs da Vercel e o limite por endereço deixaria de existir.

### Proteção contra abuso

- **Limites:** por endereço em todas as rotas do Painel (120 por minuto), na criação de sessão (5 a cada 10 minutos), por sessão (60 por minuto) e por clínica nas ações que custam (reservas, triagens, pedidos do titular). O endereço vem do `X-Forwarded-For` que o Caddy sobrescreve com o da conexão, aceito só de um salto e só da rede `edge`.
- **Teto de clínicas simultâneas:** 8 partições, e o estado `lotado` aparece para o site.
- **Expiração e limpeza garantidas:** toda clínica entra num registro em arquivo (`/var/lib/careflow-demo`) antes de existir em qualquer serviço e só sai depois de apagada. Um reinício (deploy, queda, falta de memória) começa limpando o que sobrou, e a partição só volta ao pool quando a limpeza confirma. Se o registro não grava, a sessão é recusada.
- **Interruptor sem redeploy:** `Demo:Switch:Enabled` vem de um JSON montado no container e relido sozinho. Desligado, `/demo/v1/status` responde `desligado`, as demais rotas respondem 503 com `Retry-After` e as sessões abertas terminam na rodada seguinte de limpeza. O site cai no replay. Liga e desliga pelo workflow "Interruptor do Painel ao vivo" ou por `careflow-deploy demo on|off`.

### Triagem

A demonstração pública usa as respostas gravadas do modelo (ADR-0011), copiadas para a imagem do Triage. O provedor real exige `Triage:Model:LiveCallsEnabled=true` em ambiente exposto e fica desligado no compose.

### Segredos

- **Gerados na VM** por `careflow-generate-secrets`: senhas do Postgres e do RabbitMQ, chave HS256 da demonstração, pares P-256 próprios do Notifications e do Triage para ler o cofre (o cofre aceita as duas chaves públicas), chave da auditoria, chave do Rights, par da confirmação de identidade e senha do Grafana. Ficam em `/etc/careflow/env/*.env`, root e 0600. Rodar de novo não troca nada. Nada disso passa pelo GitHub nem pelo repositório.
- **Chaves do cofre no OCI Vault** (decisão de Cleber): uma chave AES protegida por software, na cota gratuita. O cofre chama `Encrypt`/`Decrypt` com dado associado (`careflow-context`) pelo SDK oficial (`OCI.DotNetSDK.Keymanagement`, UPL 1.0 ou Apache 2.0) e se autentica como instance principal: não há chave da Oracle na VM. O id guardado ganha o prefixo `oci:`. O provedor local continua para desenvolvimento e CI.

### Deploy

- **Manual e aprovado:** workflow "Deploy da demonstração", só da main, com o environment `production` exigindo aprovação de Cleber.
- **A chave de deploy só escolhe um commit.** No `authorized_keys` do usuário `deploy` ela tem `restrict` e um comando forçado que aceita `deploy <commit>`, `rollback`, `status` e `demo on|off`. A VM baixa `deploy/` desse commit do repositório público e puxa as imagens com a mesma tag. Quem tiver a chave não manda arquivo nenhum.
- **Saúde e rollback:** `compose up --wait` e uma chamada a `/demo/v1/status` pela 443 local. Se falhar, a VM volta sozinha à versão anterior. Depois, o workflow roda o teste de fumaça contra a URL pública e pede rollback se ele falhar.

### Backup e restauração

- **Diário** (decisão de Cleber: Object Storage da Oracle, 7 dias): `pg_dump -Fc` dos sete bancos com `SHA256SUMS`, enviados por instance principal. Uma regra de ciclo de vida do bucket apaga o que passar de 7 dias.
- **Ensaio semanal:** baixa o último backup, confere os SHA256, restaura num Postgres descartável sem rede e conta as eliminações concluídas cujo paciente ainda tem chave no backup (as que uma restauração precisaria reaplicar).
- **Restauração de verdade** (`careflow-restore`): mantém o banco do Rights no ar, porque o registro de eliminações é mais novo que o backup, e restaura os outros seis. Depois reaplica cada eliminação concluída (`erasure-replay`, ADR-0012), confere que nenhum paciente eliminado tem chave no cofre e que a cadeia da auditoria de cada clínica fecha. Por fim apaga as clínicas sintéticas que voltaram. O workflow "Stack de produção" ensaia isso em todo PR com uma eliminação feita depois do backup.

## Alternativas

- **AWS ou Azure:** serviços gerenciados melhores (KMS com HSM, RDS, filas), mas custo recorrente.
- **Fly.io, Render ou Railway:** os planos grátis dormem, não comportam oito serviços com Postgres e RabbitMQ, ou cobram por memória.
- **Kubernetes (k3s) na VM:** traria rollout gradual e réplicas, mas é peça grande demais para uma VM e uma instância por serviço.
- **Segredos no GitHub enviados a cada deploy:** mais um lugar com os segredos e uma chave de deploy capaz de escrevê-los. Gerados na VM, nunca saem dela.
- **Chaves do cofre num arquivo na VM:** quem copiasse o disco e o backup leria os pacientes. Com o OCI Vault a chave mestra não sai do serviço.
- **Restaurar o Rights junto:** desfaria as eliminações concluídas depois do backup, sem ninguém saber.

## Consequências

- **Testado:**
  - interruptor e limites com testes de integração no Demo;
  - provedor do OCI Vault com testes unitários e trava das chaves locais e do modelo real;
  - em todo PR, a stack de produção de pé num runner (deploy pelo script, fumaça pela 443, interruptor, backup, ensaio e restauração com reaplicação);
  - depois de cada deploy, fumaça contra a URL pública, inclusive a porta 80 fechada.
- **Riscos residuais:**
  - **Uma VM só.** Sem réplica: queda da VM derruba o modo ao vivo, e o site cai no replay. Perder a conta da Oracle leva junto as chaves do cofre e os backups; aceitável com dados sintéticos.
  - **Segredos da VM sem cópia.** Perder o disco perde a chave da auditoria (entradas antigas deixam de ser verificáveis) e a do Rights (pacotes de até 7 dias ficam ilegíveis).
  - **Chave HS256 compartilhada** entre o Demo e os sete serviços, e um usuário só no RabbitMQ. Não há IdP; um serviço comprometido emite tokens para os outros.
  - **Rollback depois de migration** só funciona se o esquema novo continuar compatível com o código anterior.
  - **Migrations com dados sob FORCE RLS:** o dono do banco não ignora o RLS, então uma migration futura que atualize linhas precisa definir a clínica ou rodar por clínica.
  - **Auditoria sem retenção.** A cadeia das sessões cresce sem limpeza; o disco tem folga, mas fica para uma fase futura.
  - **Cota da Oracle.** A Oracle pode recuperar instâncias Always Free ociosas; o uso do Painel e dos timers evita a ociosidade, mas não há garantia contratual.
  - **Os riscos do ADR-0012** continuam (retenção sem rotina, chave do Rights sem rotação).
