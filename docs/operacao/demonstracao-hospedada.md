# Demonstração hospedada: passo a passo

Guia para pôr o backend do CareFlow no ar numa VM gratuita da Oracle e operá-lo depois (ADR-0013). Tudo o que pede login seu é feito por você. O resto já está no repositório.

> **Antes de qualquer passo:** senha, token e chave privada nunca vão para o chat, para uma issue ou para um commit. Eles ficam só em três lugares: no seu computador, nos secrets do GitHub ou dentro da VM. Os avisos abaixo repetem isso em cada passo que mexe com um deles.

O que você vai ter no fim:

- uma VM Ampere A1 (arm64, 2 OCPU e 12 GB, ou 1 OCPU e 6 GB quando falta vaga) rodando a stack com `docker compose`;
- o backend em `https://<seu-subdominio>.duckdns.org`, só pela porta 443;
- deploy por um botão no GitHub Actions, com sua aprovação, teste de fumaça e volta automática se algo falhar;
- backup diário no Object Storage, apagado depois de 7 dias, e um ensaio de restauração todo domingo.

## 1. Conta e região

1. Crie a conta em <https://www.oracle.com/cloud/free/>. A Oracle pede um cartão para verificação, mas recursos Always Free não são cobrados.
2. Escolha a **região principal** com cuidado: ela não muda depois, e os recursos Always Free só existem nela. São Paulo (`sa-saopaulo-1`) ou Vinhedo (`sa-vinhedo-1`) ficam perto do público do site.
3. Fique no plano Free Tier. Para pegar surpresa cedo, crie um orçamento em **Billing & Cost Management > Budgets** com alerta a partir de US$ 1.

## Atalho: passos 2 a 8 num comando só

No console da Oracle, abra o **Cloud Shell** (o ícone `>_` no topo da página). É um terminal que já está logado como você, então nenhuma senha ou chave da conta passa por ele. Rode:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/cleberSan/careflow/main/deploy/oracle/provision.sh)
```

O script [`deploy/oracle/provision.sh`](../../deploy/oracle/provision.sh) faz os passos 2 a 8 abaixo:

- compartimento;
- rede só com 22 e 443;
- chave SSH, criada no próprio Cloud Shell;
- VM;
- vault, com a chave de software;
- bucket com a regra de 7 dias;
- permissões da VM.

Ele só cria recursos Always Free, com tamanhos fixos. Antes de criar a VM, para se a conta já tiver outra instância ou outro disco.

A VM grátis vive sem vaga, principalmente em São Paulo, que tem um domínio de disponibilidade só. Por isso o script tenta primeiro 2 OCPU e 12 GB e, sem vaga, 1 OCPU e 6 GB, que costuma ter vaga mais fácil. A stack roda na menor, só mais devagar, e dá para crescer depois (veja [Crescer a VM](#crescer-a-vm)).

Se nenhuma das duas tiver vaga, o script cria o **robô** `careflow-cacador` e termina. O robô é uma VM mínima, também do Always Free (E2.1.Micro, 1 GB, uns 50 GB de disco), que roda o [`deploy/oracle/a1-hunter.sh`](../../deploy/oracle/a1-hunter.sh):

- tenta criar a VM a cada 5 minutos, nos dois tamanhos, de dia e de noite;
- quando consegue, ou depois de 7 dias, apaga a própria VM;
- não tem SSH nem porta aberta e não leva chave nenhuma: autentica como instance principal, com permissão só para criar e apagar VMs no compartimento `careflow`;
- deixa a última tentativa e o resultado nas tags livres da instância dele (**Compute > Instances > careflow-cacador**).

Enquanto o robô trabalha, rodar o mesmo comando de novo só mostra como ele está. Quando a VM existir, o comando apaga o robô se ele ainda estiver lá, tira a permissão dele e termina o resto. Para insistir do próprio Cloud Shell antes de chamar o robô, rode com `CAREFLOW_RETRY_MINUTES=15` na frente do `bash`.

Quando a VM existe, o script mostra e salva em `~/careflow-oracle.env` os dados que o passo 10 pede: o IP, o namespace, o endpoint do vault e o OCID da chave.

Com o atalho, pule para o passo 9. O passo 10 pode ser feito do próprio Cloud Shell, com `ssh -i ~/.ssh/careflow-oracle ubuntu@<IP>`. A chave de deploy do passo 10 também pode ser gerada lá, mas em RSA, porque o Cloud Shell roda em modo FIPS e recusa ed25519: `ssh-keygen -t rsa -b 4096 -f ~/careflow-deploy -N "" -C "github-deploy"`.

## 2. Compartimento

Em **Identity & Security > Compartments**, crie `careflow`. Tudo abaixo vai nele, para as permissões da VM ficarem restritas a ele.

## 3. Rede

1. Em **Networking > Virtual cloud networks**, use **Start VCN Wizard > Create VCN with Internet Connectivity**, no compartimento `careflow`.
2. Na **Default Security List** da sub-rede pública, deixe só estas entradas de ingress:
   - TCP 22 de `0.0.0.0/0`. O deploy roda nos servidores do GitHub, que mudam de IP; o SSH aceita só chave, e a chave de deploy só executa os comandos de deploy.
   - TCP 443 de `0.0.0.0/0`.
   - O ICMP que o assistente cria pode ficar.
3. Não abra a porta 80. O certificado sai pela própria 443.

## 4. Chave SSH para você entrar na VM

> A chave **privada** fica só no seu computador. Só a pública vai para a Oracle.

```bash
ssh-keygen -t ed25519 -f ~/.ssh/careflow-oracle -C "cleber@careflow"
```

## 5. Instância

Em **Compute > Instances > Create instance**, no compartimento `careflow`:

1. **Image:** Canonical Ubuntu 24.04 (a versão aarch64 aparece ao escolher a shape Ampere).
2. **Shape:** `VM.Standard.A1.Flex`, 2 OCPU e 12 GB.
3. **Networking:** a sub-rede pública da VCN do passo 3, com IP público.
4. **SSH keys:** cole o conteúdo de `~/.ssh/careflow-oracle.pub`.
5. **Boot volume:** 100 GB (a cota gratuita é de 200 GB no total).
6. **Advanced options > Management > Cloud-init script:** cole o arquivo [`deploy/oracle/cloud-init.yaml`](../../deploy/oracle/cloud-init.yaml) inteiro, sem mudar nada. Ele não tem segredo.

Se aparecer "Out of capacity", tente 1 OCPU e 6 GB, outro domínio de disponibilidade ou de novo mais tarde. É comum na shape gratuita.

Anote o **IP público** e o **OCID da instância** (página da instância).

## 6. Chave do cofre (OCI Vault)

1. Em **Identity & Security > Vault**, crie o vault `careflow` (não marque "virtual private vault").
2. Dentro dele, **Create key**: nome `careflow-kek`, **Protection mode: Software**, algoritmo AES, 256 bits. Chaves de software são gratuitas.
3. Anote o **Cryptographic endpoint** do vault e o **OCID da chave**. Não são segredos, mas são da sua conta: eles entram na VM no passo 10, não no repositório.

## 7. Bucket de backup

1. Em **Storage > Buckets**, crie `careflow-backups` no compartimento `careflow`, privado, tier Standard.
2. Em **Lifecycle Policy Rules**, crie uma regra: ação **Delete**, objetos com mais de **7 dias**.
3. Anote o **Namespace** que aparece na página do bucket.

## 8. Permissões da VM (instance principal)

A VM fala com o Vault e com o bucket sem nenhuma chave guardada nela. Ela se identifica pela própria instância.

1. Em **Identity & Security > Domains > Default > Dynamic groups**, crie `careflow-vm` com a regra:

   ```text
   instance.id = '<OCID da instância>'
   ```

2. Em **Identity & Security > Policies**, no compartimento `careflow`, crie `careflow-vm` com:

   ```text
   Allow dynamic-group careflow-vm to use keys in compartment careflow where target.key.id = '<OCID da chave>'
   Allow dynamic-group careflow-vm to read buckets in compartment careflow where target.bucket.name = 'careflow-backups'
   Allow dynamic-group careflow-vm to manage objects in compartment careflow where target.bucket.name = 'careflow-backups'
   Allow service objectstorage-<sua-região> to manage object-family in compartment careflow
   ```

   A última linha deixa a regra de ciclo de vida do bucket apagar os backups antigos. Troque `<sua-região>` pelo id da região, por exemplo `objectstorage-sa-saopaulo-1`.

## 9. Domínio grátis (DuckDNS)

> O token do DuckDNS é um segredo. Ele é digitado escondido dentro da VM no passo 10 e não vai para nenhum outro lugar.

1. Entre em <https://www.duckdns.org> e crie um subdomínio, por exemplo `careflow-cleber`.
2. Aponte o subdomínio para o IP público da VM. Depois, a VM mantém o IP atualizado sozinha.

## 10. Configurar a VM

> Este passo pede o token do DuckDNS. Digite-o só no terminal da VM, quando ele perguntar.

```bash
ssh -i ~/.ssh/careflow-oracle ubuntu@<IP público>
cloud-init status --wait          # espera a preparação terminar
```

Antes de rodar o setup, gere no **seu computador** a chave que o GitHub vai usar para o deploy:

```bash
ssh-keygen -t ed25519 -f ~/careflow-deploy -N "" -C "github-deploy"
cat ~/careflow-deploy.pub         # esta linha (pública) vai para o setup
```

Na VM:

```bash
sudo careflow-setup
```

Ele pergunta, nesta ordem: subdomínio e token do DuckDNS, endereço do site, seu e-mail para avisos do Let's Encrypt, namespace e nome do bucket, endpoint do vault, OCID da chave e a linha da chave pública de deploy. No fim, gera todos os segredos da stack dentro da VM (`/etc/careflow/env`, só root lê) e liga os timers de backup, ensaio e DuckDNS.

Ainda na VM, gere a linha que o GitHub vai usar para reconhecer a VM (é pública):

```bash
echo "<seu-subdominio>.duckdns.org $(cut -d' ' -f1,2 /etc/ssh/ssh_host_ed25519_key.pub)"
```

## 11. GitHub

> A chave **privada** de deploy vai só para o secret do environment. Depois de salvar, apague o arquivo do seu computador.

No repositório `careflow`, **Settings > Environments > New environment**, nome `production`:

1. **Required reviewers:** você. **Deployment branches:** só `main`.
2. **Environment secrets:** `DEPLOY_SSH_KEY` com o conteúdo de `~/careflow-deploy` (o arquivo sem `.pub`). Depois: `rm ~/careflow-deploy`.
3. **Environment variables:**
   - `CAREFLOW_DEMO_HOST`: `<seu-subdominio>.duckdns.org`
   - `CAREFLOW_SITE_ORIGIN`: o endereço do site, por exemplo `https://portfolio-web-psi-rust.vercel.app`
   - `DEPLOY_KNOWN_HOSTS`: a linha gerada no fim do passo 10

## 12. Imagens públicas

Depois do merge na main, o workflow **Imagens** publica `ghcr.io/clebersan/careflow`. Na primeira vez, abra o pacote em **github.com/cleberSan > Packages > careflow > Package settings > Change visibility** e escolha **Public**. Assim a VM puxa as imagens sem senha do GitHub.

## 13. Primeiro deploy

**Actions > Deploy da demonstração > Run workflow** (deixe o commit vazio para o último da main) e aprove quando o GitHub pedir. O workflow:

1. confere que as oito imagens do commit existem;
2. pede à VM `deploy <commit>`: ela baixa `deploy/` desse commit, roda as migrations, sobe os serviços e confere a saúde, voltando sozinha à versão anterior se algo falhar;
3. roda o teste de fumaça contra a URL pública e, se falhar, pede rollback.

Para conferir à mão:

```bash
curl -H 'X-CareFlow-Demo: 1' https://<seu-subdominio>.duckdns.org/demo/v1/status
```

Depois, configure o site com o endereço do backend (PR do site da fase 9).

## Operação do dia a dia

| O quê | Como |
|---|---|
| Desligar ou ligar o Painel ao vivo | **Actions > Interruptor do Painel ao vivo**, ou `sudo careflow-deploy demo off` na VM. Desligado, o site mostra a gravação. |
| Ver versão e containers | `sudo careflow-deploy status` |
| Voltar para a versão anterior | `sudo careflow-deploy rollback` |
| Grafana | `ssh -i ~/.ssh/careflow-oracle -L 3000:127.0.0.1:3000 ubuntu@<IP>` e abra <http://localhost:3000>. Usuário `cleber`; a senha fica na VM: `sudo grep GF_SECURITY_ADMIN_PASSWORD /etc/careflow/env/grafana.env`. |
| Backups | `systemctl list-timers 'careflow-*'` e `journalctl -u careflow-backup` |
| Ensaio de restauração | roda todo domingo; resultado em `journalctl -u careflow-restore-drill`. À mão: `sudo careflow-restore-drill` |
| Trocar o endpoint ou a chave do vault | `sudo careflow-setup` de novo e `sudo careflow-deploy redeploy` |

## Crescer a VM

Se a VM saiu com 1 OCPU e 6 GB porque faltou vaga, dá para passar para 2 OCPU e 12 GB depois, sem sair do Always Free. A VM reinicia sozinha, e o Painel ao vivo fica fora por alguns minutos (o site mostra a gravação nesse tempo). No Cloud Shell:

```bash
source ~/careflow-oracle.env
oci compute instance update --instance-id "$INSTANCE_OCID" --shape-config '{"ocpus":2,"memoryInGBs":12}' --force
```

Se a Oracle responder "Out of host capacity", a VM continua como estava; tente de novo outro dia. O swap de 4 GB criado para a VM menor fica, sem prejuízo.

## Restaurar um backup

> Isto sobrescreve os bancos no ar. Desliga o Painel ao vivo durante a restauração.

```bash
sudo careflow-restore-drill                          # confere antes que o backup restaura
sudo careflow-restore <AAAAMMDDTHHMMSSZ> --confirm
```

O banco do Rights no ar é mantido, porque ele guarda as eliminações concluídas depois do backup. O script restaura os outros seis, reaplica cada eliminação, confere que o cofre não tem mais a chave de nenhum paciente eliminado e que a cadeia da auditoria fecha, e apaga as clínicas sintéticas que voltaram. Se o banco do Rights também se perdeu, acrescente `--include-rights`: as eliminações feitas depois do backup se perdem junto (ADR-0013).

## Se a VM se perder

Os segredos da stack não têm cópia fora da VM. Numa VM nova: passos 5, 8 (OCID novo no dynamic group), 10 e 13. Os pacientes voltam do backup com a mesma chave do vault, mas entradas antigas da auditoria deixam de ser verificáveis e pacotes do titular ainda não baixados ficam ilegíveis. Com dados sintéticos isso é aceitável.

## Custos

Para continuar sem custo, não mude a conta para **Pay As You Go** (o botão "Upgrade"). Numa conta Free Tier, a Oracle não deixa criar recurso pago depois dos 30 dias de teste, então o pior caso é perder a VM, não receber cobrança.

Tudo acima cabe no Always Free: uma VM A1 dentro da cota de 2 OCPU e 12 GB (a Oracle cortou de 4 OCPU e 24 GB em junho de 2026), o robô numa das duas VMs E2.1.Micro gratuitas enquanto ele existir, até 200 GB de disco (a VM usa 100 e o robô uns 50), chaves de software no Vault e até 20 GB no Object Storage (os backups têm poucos MB). A Oracle pode recuperar instâncias Always Free ociosas por muito tempo; o uso do Painel e os timers costumam evitar isso, mas não há garantia.
