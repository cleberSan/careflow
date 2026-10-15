# Como contribuir

## Fluxo

1. Crie uma branch a partir de `main` (`feat/...`, `fix/...`, `chore/...`, `docs/...`).
2. Use [Conventional Commits](https://www.conventionalcommits.org/pt-br/) nas mensagens.
3. Abra um PR usando o template; o CI precisa estar verde.
4. Decisões arquiteturais entram como ADR em `docs/adr/`.

## Rodando localmente

Pré-requisitos: .NET SDK 10 (versão em `global.json`) e Docker (os testes de integração sobem PostgreSQL e, nos testes de ponta a ponta, RabbitMQ com Testcontainers).

```bash
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release --coverlet --coverlet-output-format cobertura
```

Para usar um PostgreSQL já existente nos testes de integração, defina `CAREFLOW_TEST_POSTGRES` com a connection string.

`tests/CareFlow.EndToEndTests` sobe os quatro serviços num processo, com PostgreSQL e RabbitMQ reais, e cobre a propagação da revogação, broker fora do ar, duplicatas, desordem e mensagens mortas.

### Banco e broker locais

```bash
docker compose up -d
```

O script `deploy/postgres/init-dev.sql` cria um banco por serviço, os logins das APIs (`*_api`) e dos despachantes de mensagens (`*_dispatcher`), todos sem superusuário, para que o Row-Level Security valha também em desenvolvimento. As migrations rodam com o papel `careflow` (connection string `...Migrations`). Se o seu volume é anterior à fase 4, recrie-o uma vez: `docker compose down -v && docker compose up -d`.

O RabbitMQ local fica em `localhost:5672` (painel em `http://localhost:15672`, usuário `careflow`, senha `careflow-dev`, só para desenvolvimento). Com o broker fora, os serviços continuam atendendo e os eventos esperam no Outbox (ADR-0006).

### Subindo a API de agenda

```bash
dotnet run --project src/Services/Scheduling/CareFlow.Scheduling.Api
```

Em desenvolvimento a API aplica as migrations na subida. Para chamar os endpoints, gere um token local (a chave fica nos user secrets, fora do repositório):

```bash
dotnet user-jwts create --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --scope scheduling.write --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Há requisições de exemplo em `CareFlow.Scheduling.Api.http`.

### Subindo o Patient Vault

O cofre precisa de uma chave mestra local (KEK) de 32 bytes, guardada nos user secrets, fora do repositório:

```bash
dotnet user-secrets set "KeyManagement:Local:Keys:dev-1" "$(openssl rand -base64 32)" \
  --project src/Services/PatientVault/CareFlow.PatientVault.Api
dotnet run --project src/Services/PatientVault/CareFlow.PatientVault.Api
dotnet user-jwts create --project src/Services/PatientVault/CareFlow.PatientVault.Api \
  --scope "vault.read vault.write" --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Perder essa chave torna ilegíveis os pacientes cadastrados localmente. O provedor local é recusado em `Production`. Há requisições de exemplo, só com dados sintéticos, em `CareFlow.PatientVault.Api.http`.

### Subindo Consent e Audit

```bash
dotnet run --project src/Services/Consent/CareFlow.Consent.Api
dotnet user-jwts create --project src/Services/Consent/CareFlow.Consent.Api \
  --scope "consent.read consent.write" --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token

# A auditoria assina a cadeia com uma chave HMAC de 32 bytes, guardada fora do banco e do repositório.
dotnet user-secrets set "Audit:Chain:Keys:dev-1" "$(openssl rand -base64 32)" \
  --project src/Services/Audit/CareFlow.Audit.Api
dotnet run --project src/Services/Audit/CareFlow.Audit.Api
dotnet user-jwts create --project src/Services/Audit/CareFlow.Audit.Api \
  --scope audit.read --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Perder a chave da auditoria impede verificar as entradas assinadas com ela (ADR-0007).

### Subindo o Notifications

O Notifications lê telefone e e-mail no cofre na hora do envio, com um token de serviço curto por clínica (ADR-0009). Gere um par de chaves P-256 local: a privada fica nos user secrets do Notifications, a pública nos do cofre. O emissor local é recusado em `Production`.

```bash
openssl ecparam -name prime256v1 -genkey -noout -out /tmp/careflow-service-token.pem
dotnet user-secrets set "ServiceTokens:Local:PrivateKey" \
  "$(openssl pkcs8 -topk8 -nocrypt -in /tmp/careflow-service-token.pem -outform DER | base64 -w0)" \
  --project src/Services/Notifications/CareFlow.Notifications.Api
dotnet user-secrets set "InternalAuth:PublicKey" \
  "$(openssl ec -in /tmp/careflow-service-token.pem -pubout -outform DER 2>/dev/null | base64 -w0)" \
  --project src/Services/PatientVault/CareFlow.PatientVault.Api
rm /tmp/careflow-service-token.pem

dotnet run --project src/Services/Notifications/CareFlow.Notifications.Api
dotnet user-jwts create --project src/Services/Notifications/CareFlow.Notifications.Api \
  --scope "notifications.read notifications.admin" --claim tenant_id=11111111-1111-1111-1111-111111111111 -o token
```

Os provedores são simulados: as mensagens ficam em memória, nada sai da máquina. Para ver o fallback, ligue a falha injetada no `appsettings.Development.json` (`FaultInjection:Enabled` e o modo do canal: `Latency`, `Error`, `Unavailable` ou `LostResponse`); a mudança vale sem reiniciar.

### Observabilidade local

Collector, Tempo, Loki, Prometheus e Grafana sobem num perfil à parte do compose, só no loopback (ADR-0010):

```bash
docker compose --profile observability up -d
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317   # em cada terminal que roda um serviço
```

Sem essa variável os serviços não exportam nada. Abra http://localhost:3000: o painel inicial é "CareFlow · Visão geral" (latência por endpoint, Outbox, filas e mensagens mortas, atraso de entrega); "CareFlow · Privacidade e resiliência" mostra a propagação da revogação, envios e fallbacks por canal e o circuit breaker. Traces ficam no Explore com o Tempo, e de cada span dá para pular para os logs do mesmo trace no Loki. O login de administrador local é `admin`/`careflow-dev`; os dashboards vêm do repositório e se editados na interface não são salvos: altere o JSON em `deploy/observability/grafana/dashboards`.

Os serviços já tiram dados pessoais da telemetria antes de exportar (caminhos viram o template da rota, CPF, e-mail e telefone viram marcadores). O Collector repete a regra como segunda barreira. Spans novos levam só pseudônimos mascarados (`LogMask.Pseudonym`), nunca nome, contato ou o identificador completo, e o teste `TelemetryPersonalDataTests` falha se algo escapar.

### Subindo o Demo (backend do Painel)

O Demo é o BFF do Painel de Privacidade e Resiliência (ADR-0010): cria uma clínica e um paciente sintéticos por visitante, chama os outros serviços com tokens curtos de um emissor local (HS256) e apaga os dados da clínica no fim da sessão. Não sobe em `Production`. Gere uma chave e entregue a mesma ao Demo e a cada serviço; o `appsettings.Development.json` dos serviços já aceita o emissor `careflow-demo-local` e as audiências `careflow-*`.

```bash
DEMO_KEY="$(openssl rand -base64 32)"
dotnet user-secrets set "Demo:Tokens:SigningKey" "$DEMO_KEY" --project src/Services/Demo/CareFlow.Demo.Api
for service in PatientVault Consent Scheduling Notifications Audit; do
  project="src/Services/$service/CareFlow.$service.Api"
  dotnet user-secrets set "Authentication:Schemes:Bearer:SigningKeys:9:Issuer" "careflow-demo-local" --project "$project"
  dotnet user-secrets set "Authentication:Schemes:Bearer:SigningKeys:9:Value" "$DEMO_KEY" --project "$project"
  dotnet user-secrets set "Demo:PurgeEnabled" "true" --project "$project"
done
unset DEMO_KEY

dotnet run --project src/Services/Demo/CareFlow.Demo.Api
```

O índice 9 evita sobrescrever a chave do `dotnet user-jwts` (índice 0). Para o Notifications aceitar partições isoladas, ligue `FaultInjection:Enabled`, `FaultInjection:ClinicOverridesEnabled` e `FaultInjection:IsolatedSlots` (por exemplo 8) nos user secrets dele. Com a stack de observabilidade de pé, o Demo lê traces do Tempo e logs do Loki; sem ela, o Painel mostra que a telemetria não está disponível. A sessão é um cookie `__Host-` e toda chamada precisa do cabeçalho `X-CareFlow-Demo: 1`; exemplos em `CareFlow.Demo.Api.http`.

### Gravação do Painel

O site não chama o Demo: ele reproduz uma gravação real dos quatro cenários. O teste `PanelRecordingTests` grava o arquivo quando a variável `CAREFLOW_PANEL_RECORDING` aponta para um caminho; o CI publica o resultado como artefato `panel-recording`.

```bash
CAREFLOW_PANEL_RECORDING=$PWD/panel.json CAREFLOW_COMMIT=$(git rev-parse HEAD) \
  dotnet test --project tests/CareFlow.EndToEndTests -c Release -- --filter-class '*PanelRecordingTests'
```

Na gravação, CPF e telefone sintéticos saem só com os dois últimos dígitos, e o teste falha se algum dado pessoal aparecer fora da visão do cofre.

### Migrations

```bash
dotnet tool restore
dotnet ef migrations add NomeDaMigration --project src/Services/Scheduling/CareFlow.Scheduling.Api \
  --output-dir Infrastructure/Persistence/Migrations
```

Toda tabela nova com `tenant_id` precisa de `EnableTenantRowLevelSecurity` na migration (ADR-0003). Outbox e Inbox usam `EnableOutboxSecurity` e `EnableInboxSecurity`, que também liberam o papel do despachante (ADR-0006).

### Eventos

- Publique pelo Outbox (`db.Enqueue(...)` ou `db.RecordAccess(...)`) antes do `SaveChanges`, nunca direto no broker: o evento e a mudança gravam juntos ou não gravam.
- Consumidores implementam `IIntegrationEventConsumer<TEvent>` e precisam tolerar desordem; duplicatas o Inbox já descarta.
- Eventos carregam só pseudônimos. O tipo do evento não muda depois de publicado: crie um tipo novo (`.v2`).

## Padrões

- Testes no padrão AAA (Arrange, Act, Assert), cobrindo cenários felizes, de erro e limites.
- Regras de negócio retornam `Result`; não use exceptions para fluxo.
- Nunca registre dados pessoais em logs, mensagens de erro ou testes. Use dados sintéticos (`SyntheticPatients` em `tests/CareFlow.Testing`).
