extern alias audit;
extern alias consent;
extern alias demo;
extern alias notifications;
extern alias rights;
extern alias scheduling;
extern alias triage;
extern alias vault;

using System.Security.Cryptography;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.EndToEndTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;

using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(CareFlowSystem))]

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>
/// O sistema inteiro num processo: PostgreSQL e RabbitMQ reais (Testcontainers), um banco por serviço e os
/// sete serviços com mensageria ligada, cada um com o login da API e o do despachante, sem superusuário.
/// Os logs de todos os serviços são capturados para conferir que nenhum carrega dado pessoal.
/// O Notifications lê o contato no cofre por HTTP (o cofre roda em Kestrel) com token de serviço
/// ES256 de verdade, e sobe com o WhatsApp derrubado por falha injetada: o fallback é o caminho normal aqui.
/// </summary>
public sealed class CareFlowSystem : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _factories = [];
    private PostgresTestServer? _postgres;
    private RabbitMqContainer? _rabbit;

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Spans, logs e métricas como seriam exportados por OTLP, depois dos filtros de cada serviço.</summary>
    public TelemetrySink Telemetry { get; } = new();

    public ServiceHost Vault { get; private set; } = null!;

    public ServiceHost Consent { get; private set; } = null!;

    public ServiceHost Scheduling { get; private set; } = null!;

    public ServiceHost Audit { get; private set; } = null!;

    public ServiceHost Notifications { get; private set; } = null!;

    /// <summary>Triagem com IA reproduzindo as respostas gravadas do modelo (<c>eval/triage/recordings</c>, ADR-0011).</summary>
    public ServiceHost Triage { get; private set; } = null!;

    /// <summary>Pedidos do titular (acesso, portabilidade e eliminação), ADR-0012.</summary>
    public ServiceHost Rights { get; private set; } = null!;

    /// <summary>O backend do Painel, falando com os serviços como faria fora dos testes.</summary>
    public WebApplicationFactory<demo::Program> Demo { get; private set; } = null!;

    public BrokerProbe Broker { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _rabbit = new RabbitMqBuilder("rabbitmq:4.1-alpine").Build();
        await _rabbit.StartAsync();
        var brokerUri = _rabbit.GetConnectionString();
        Broker = await BrokerProbe.ConnectAsync(_rabbit, brokerUri);

        using var serviceTokenKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // O código de confirmação de identidade vai cifrado do serviço de pedidos (chave pública) ao Notifications.
        using var identityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Todos rodam em Kestrel de verdade: quem chama outro serviço (Notifications no cofre, o Demo em todos) usa
        // o SocketsHttpHandler, que é quem propaga o traceparent e cria o span de cliente, como em produção.
        Vault = await StartAsync<vault::Program>(
            new ServiceSpec("patient-vault", "PatientVault", "careflow_vault", "vault_app", "vault_relay"),
            brokerUri,
            kestrel: true,
            configureServices: null,
            ("InternalAuth:PublicKey", Convert.ToBase64String(serviceTokenKey.ExportSubjectPublicKeyInfo())),
            ("KeyManagement:Provider", "Local"),
            ("KeyManagement:Local:ActiveKeyId", "e2e-1"),
            ("KeyManagement:Local:Keys:e2e-1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Consent = await StartAsync<consent::Program>(
            new ServiceSpec("consent", "Consent", "careflow_consent", "consent_app", "consent_relay"),
            brokerUri);
        Scheduling = await StartAsync<scheduling::Program>(
            new ServiceSpec("scheduling", "Scheduling", "careflow_scheduling", "scheduling_app", "scheduling_relay"),
            brokerUri);
        Audit = await StartAsync<audit::Program>(
            new ServiceSpec("audit", "Audit", "careflow_audit", "audit_app", "audit_relay"),
            brokerUri,
            ("Audit:Chain:ActiveKeyId", "e2e-1"),
            ("Audit:Chain:Keys:e2e-1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        Notifications = await StartAsync<notifications::Program>(
            new ServiceSpec("notifications", "Notifications", "careflow_notifications", "notifications_app", "notifications_relay"),
            brokerUri,
            ("Vault:BaseAddress", Vault.BaseAddress!.ToString()),
            ("ServiceTokens:Local:PrivateKey", Convert.ToBase64String(serviceTokenKey.ExportPkcs8PrivateKey())),
            ("Notifications:PollInterval", "00:00:00.200"),
            ("Notifications:RoundRetryDelay", "00:00:00.200"),
            ("Notifications:MaxRoundRetryDelay", "00:00:01"),
            ("Notifications:Resilience:AttemptTimeout", "00:00:01"),
            ("Notifications:Resilience:TotalTimeout", "00:00:05"),
            ("Notifications:Resilience:RetryBaseDelay", "00:00:00.050"),
            ("FaultInjection:Enabled", "true"),
            ("FaultInjection:Channels:whatsapp:Mode", "Unavailable"),
            ("Notifications:Resilience:BreakDuration", "00:00:03"),
            ("FaultInjection:ClinicOverridesEnabled", "true"),
            ("FaultInjection:IsolatedSlots", "16"),
            ("IdentityVerification:PrivateKey", Convert.ToBase64String(identityKey.ExportPkcs8PrivateKey())),
            ("Demo:SimulatedInboxEnabled", "true"));

        // O Triage lê idade e nome no cofre com o mesmo par de chaves de serviço (um por serviço em produção) e
        // nunca chama a rede para o modelo: só reproduz as gravações versionadas.
        Triage = await StartAsync<triage::Program>(
            new ServiceSpec("triage", "Triage", "careflow_triage", "triage_app", "triage_relay"),
            brokerUri,
            ("Vault:BaseAddress", Vault.BaseAddress!.ToString()),
            ("ServiceTokens:Local:PrivateKey", Convert.ToBase64String(serviceTokenKey.ExportPkcs8PrivateKey())),
            ("Triage:Model:Provider", "Recorded"),
            ("Triage:Model:RecordingsPath", RepositoryPaths.TriageRecordings));

        // A limpeza dos pacotes vencidos roda pelos testes (no tempo deles), não por um timer.
        Rights = await StartAsync<rights::Program>(
            new ServiceSpec("rights", "Rights", "careflow_rights", "rights_app", "rights_relay"),
            brokerUri,
            ("Rights:Keys:KeyEncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            ("IdentityVerification:NotificationsPublicKey", SealedBox.ExportPublicKey(identityKey)),
            ("Rights:SweepPackages", "false"));

        // Eventos publicados antes de a fila do consumidor existir iriam para a fila de não roteados.
        await Broker.WaitForQueuesAsync(
            "careflow.scheduling", "careflow.audit", "careflow.notifications", "careflow.triage", "careflow.rights",
            "careflow.patient-vault", "careflow.consent");

        Demo = StartDemo();
    }

    /// <summary>
    /// Sobe uma instância do Demo ligada aos serviços deste sistema. Os serviços autenticam pelos cabeçalhos de
    /// teste (no lugar do token HS256 local) e os traces vêm do que foi exportado aqui (no lugar do Tempo).
    /// </summary>
    public WebApplicationFactory<demo::Program> StartDemo(params (string Key, string Value)[] overrides)
    {
        var hosts = new Dictionary<string, ServiceHost>(StringComparer.Ordinal)
        {
            ["vault"] = Vault,
            ["consent"] = Consent,
            ["scheduling"] = Scheduling,
            ["notifications"] = Notifications,
            ["audit"] = Audit,
            ["triage"] = Triage,
            ["rights"] = Rights,
        };

        var factory = new WebApplicationFactory<demo::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Demo:Tokens:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Demo:SessionLifetime", "00:05:00");
            builder.UseSetting("Demo:Slots", "8");
            builder.UseSetting("Demo:SlotCooldown", "00:00:00");
            builder.UseSetting("Demo:CleanupInterval", "00:00:00.300");
            builder.UseSetting("Demo:EligibilityWait", "00:00:20");
            builder.UseSetting("Demo:WhatsAppFaultDuration", "00:02:00");
            builder.UseSetting("Demo:AllowedOrigins:0", DemoClient.SiteOrigin);
            builder.UseSetting("Demo:RateLimits:SessionsPerWindow", "1000");
            builder.UseSetting("Demo:RateLimits:RequestsPerMinute", "1000");
            foreach (var (name, host) in hosts)
            {
                builder.UseSetting($"Demo:Services:{char.ToUpperInvariant(name[0])}{name[1..]}", host.BaseAddress!.ToString());
            }

            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                Telemetry.Attach(services, "careflow-demo");
                services.AddSingleton<demo::CareFlow.Demo.Api.Services.IServiceAuthenticator, TestHeadersAuthenticator>();
                services.AddSingleton<demo::CareFlow.Demo.Api.Telemetry.ITelemetryReader>(new SinkTelemetryReader(Telemetry));
            });
        });
        _factories.Add(factory);
        TelemetrySink.CurrentService.Value = "careflow-demo";
        factory.StartServer();
        TelemetrySink.CurrentService.Value = null;
        return factory;
    }

    private Task<ServiceHost> StartAsync<TProgram>(ServiceSpec spec, string brokerUri, params (string Key, string Value)[] settings)
        where TProgram : class =>
        StartAsync<TProgram>(spec, brokerUri, kestrel: true, configureServices: null, settings);

    private async Task<ServiceHost> StartAsync<TProgram>(
        ServiceSpec spec,
        string brokerUri,
        bool kestrel,
        Action<IServiceCollection>? configureServices,
        params (string Key, string Value)[] settings)
        where TProgram : class
    {
        var postgres = _postgres!;
        var adminConnectionString = await postgres.CreateDatabaseAsync(spec.Database);

        // Papéis e logins antes da subida: o despachante e o consumidor começam a trabalhar junto com o host.
        await postgres.EnsureRoleAsync(spec.ApplicationRole);
        await postgres.EnsureRoleAsync(spec.RelayRole);
        var api = postgres.NewApplicationLogin($"{spec.ApplicationRole}_e2e", spec.Database);
        var relay = postgres.NewApplicationLogin($"{spec.RelayRole}_e2e", spec.Database);
        await postgres.CreateLoginAsync(api, spec.ApplicationRole);
        await postgres.CreateLoginAsync(relay, spec.RelayRole);

        var factory = new WebApplicationFactory<TProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting($"ConnectionStrings:{spec.ConnectionStringName}", api.ConnectionString);
            builder.UseSetting($"ConnectionStrings:{spec.ConnectionStringName}Migrations", adminConnectionString);
            builder.UseSetting($"ConnectionStrings:{spec.ConnectionStringName}Relay", relay.ConnectionString);
            builder.UseSetting("ConnectionStrings:RabbitMq", brokerUri);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Demo:PurgeEnabled", "true");
            builder.UseSetting("Messaging:Enabled", "true");
            builder.UseSetting("Messaging:RelayPollInterval", "00:00:00.200");
            builder.UseSetting("Messaging:MaxRetryDelay", "00:00:02");
            builder.UseSetting("Messaging:PublishTimeout", "00:00:03");
            builder.UseSetting("Messaging:DeliveryLimit", "3");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.AddTestAuthentication();
                Telemetry.Attach(services, "careflow-" + spec.ServiceName);
                configureServices?.Invoke(services);
            });
        });
        _factories.Add(factory);
        if (kestrel)
        {
            // Porta livre escolhida pelo sistema, só no loopback.
            factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        }

        // Sobe o host: aplica as migrations (com o admin do banco) e inicia despachante e consumidor. O que ele
        // começa em segundo plano (despachante, consumidor, envios) fica no contexto deste serviço.
        TelemetrySink.CurrentService.Value = "careflow-" + spec.ServiceName;
        factory.StartServer();
        TelemetrySink.CurrentService.Value = null;
        Telemetry.Track(factory.Services.GetService<MeterProvider>());
        Uri? baseAddress = null;
        if (kestrel)
        {
            var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            baseAddress = new Uri(address.Replace("[::]", "localhost", StringComparison.Ordinal).TrimEnd('/') + "/");
        }

        return new ServiceHost(spec, factory.CreateClient, adminConnectionString, factory.Services)
        {
            BaseAddress = baseAddress,
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        if (Broker is not null)
        {
            await Broker.DisposeAsync();
        }

        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        Logs.Dispose();
    }
}

/// <summary>
/// Todas as classes que usam o sistema ficam numa coleção só, então rodam em sequência: um teste derruba o
/// broker, e rodar em paralelo misturaria as janelas de indisponibilidade com os outros fluxos.
/// </summary>
public static class SequentialFlows
{
    public const string Name = "sistema-completo";
}

/// <summary>Caminhos do repositório, achados a partir da saída do build (a raiz tem o <c>CareFlow.slnx</c>).</summary>
public static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string TriageCases => Path.Combine(Root, "eval", "triage", "cases.json");

    public static string TriageRecordings => Path.Combine(Root, "eval", "triage", "recordings");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CareFlow.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Raiz do repositório (CareFlow.slnx) não encontrada.");
    }
}

public sealed record ServiceSpec(string ServiceName, string ConnectionStringName, string Database, string ApplicationRole, string RelayRole);

/// <summary>Um serviço em execução: clientes HTTP com identidade de teste e acesso de admin ao banco dele.</summary>
public sealed class ServiceHost(ServiceSpec spec, Func<HttpClient> createClient, string adminConnectionString, IServiceProvider services)
{
    public ServiceSpec Spec { get; } = spec;

    public string AdminConnectionString { get; } = adminConnectionString;

    public IServiceProvider Services { get; } = services;

    /// <summary>Endereço real, quando o serviço roda em Kestrel (para outro serviço chamá-lo por HTTP).</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Cliente sem identidade nenhuma (sem os cabeçalhos de teste): só passa em endpoints anônimos.</summary>
    public HttpClient AnonymousClient() => createClient();

    public HttpClient Client(Guid tenantId, params string[] scopes)
    {
        var client = createClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, string.Join(' ', scopes));
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return client;
    }
}
