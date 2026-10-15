extern alias audit;
extern alias consent;
extern alias notifications;
extern alias scheduling;
extern alias vault;

using System.Security.Cryptography;

using CareFlow.EndToEndTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(CareFlowSystem))]

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>
/// O sistema inteiro num processo: PostgreSQL e RabbitMQ reais (Testcontainers), um banco por serviço e os
/// cinco serviços com mensageria ligada, cada um com o login da API e o do despachante, sem superusuário.
/// Os logs de todos os serviços são capturados para conferir que nenhum carrega dado pessoal.
/// O Notifications lê o contato no cofre por HTTP (o handler do TestServer do cofre) com token de serviço
/// ES256 de verdade, e sobe com o WhatsApp derrubado por falha injetada: o fallback é o caminho normal aqui.
/// </summary>
public sealed class CareFlowSystem : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _factories = [];
    private PostgresTestServer? _postgres;
    private RabbitMqContainer? _rabbit;

    public CapturingLoggerProvider Logs { get; } = new();

    public ServiceHost Vault { get; private set; } = null!;

    public ServiceHost Consent { get; private set; } = null!;

    public ServiceHost Scheduling { get; private set; } = null!;

    public ServiceHost Audit { get; private set; } = null!;

    public ServiceHost Notifications { get; private set; } = null!;

    public BrokerProbe Broker { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _rabbit = new RabbitMqBuilder("rabbitmq:4.1-alpine").Build();
        await _rabbit.StartAsync();
        var brokerUri = _rabbit.GetConnectionString();
        Broker = await BrokerProbe.ConnectAsync(_rabbit, brokerUri);

        using var serviceTokenKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Vault = await StartAsync<vault::Program>(
            new ServiceSpec("patient-vault", "PatientVault", "careflow_vault", "vault_app", "vault_relay"),
            brokerUri,
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

        var vaultHandler = Vault.CreateHandler;
        Notifications = await StartAsync<notifications::Program>(
            new ServiceSpec("notifications", "Notifications", "careflow_notifications", "notifications_app", "notifications_relay"),
            brokerUri,
            services => services
                .AddHttpClient(notifications::CareFlow.Notifications.Api.Contacts.VaultContactDirectory.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => vaultHandler()),
            ("Vault:BaseAddress", "http://localhost/"),
            ("ServiceTokens:Local:PrivateKey", Convert.ToBase64String(serviceTokenKey.ExportPkcs8PrivateKey())),
            ("Notifications:PollInterval", "00:00:00.200"),
            ("Notifications:RoundRetryDelay", "00:00:00.200"),
            ("Notifications:MaxRoundRetryDelay", "00:00:01"),
            ("Notifications:Resilience:AttemptTimeout", "00:00:01"),
            ("Notifications:Resilience:TotalTimeout", "00:00:05"),
            ("Notifications:Resilience:RetryBaseDelay", "00:00:00.050"),
            ("FaultInjection:Enabled", "true"),
            ("FaultInjection:Channels:whatsapp:Mode", "Unavailable"));

        // Eventos publicados antes de a fila do consumidor existir iriam para a fila de não roteados.
        await Broker.WaitForQueuesAsync("careflow.scheduling", "careflow.audit", "careflow.notifications");
    }

    private Task<ServiceHost> StartAsync<TProgram>(ServiceSpec spec, string brokerUri, params (string Key, string Value)[] settings)
        where TProgram : class =>
        StartAsync<TProgram>(spec, brokerUri, configureServices: null, settings);

    private async Task<ServiceHost> StartAsync<TProgram>(
        ServiceSpec spec,
        string brokerUri,
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
                configureServices?.Invoke(services);
            });
        });
        _factories.Add(factory);

        // Sobe o host: aplica as migrations (com o admin do banco) e inicia despachante e consumidor.
        _ = factory.Server;
        return new ServiceHost(spec, factory.CreateClient, adminConnectionString, factory.Services)
        {
            CreateHandler = () => factory.Server.CreateHandler(),
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

public sealed record ServiceSpec(string ServiceName, string ConnectionStringName, string Database, string ApplicationRole, string RelayRole);

/// <summary>Um serviço em execução: clientes HTTP com identidade de teste e acesso de admin ao banco dele.</summary>
public sealed class ServiceHost(ServiceSpec spec, Func<HttpClient> createClient, string adminConnectionString, IServiceProvider services)
{
    public ServiceSpec Spec { get; } = spec;

    public string AdminConnectionString { get; } = adminConnectionString;

    public IServiceProvider Services { get; } = services;

    /// <summary>Handler HTTP que entrega direto no TestServer do serviço, para outro serviço chamá-lo.</summary>
    public required Func<HttpMessageHandler> CreateHandler { get; init; }

    public HttpClient Client(Guid tenantId, params string[] scopes)
    {
        var client = createClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, string.Join(' ', scopes));
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return client;
    }
}
