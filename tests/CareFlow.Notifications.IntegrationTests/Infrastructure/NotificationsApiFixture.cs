using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts;
using CareFlow.Contracts.Consents;
using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Channels.Simulated;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Api.Delivery;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

using Npgsql;

[assembly: AssemblyFixture(typeof(NotificationsApiFixture))]

// Falhas injetadas e circuit breakers são do processo inteiro: os testes rodam em sequência para que a queda
// do WhatsApp num teste não vaze para outro.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace CareFlow.Notifications.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real e uma instância do Notifications. Migrations com admin, API e despachante com logins sem
/// privilégios (RLS vale), mensageria e despachante automático desligados: os testes entregam eventos pelo
/// processador do Inbox e rodam ciclos do despachante quando querem. Os provedores são os simulados de
/// verdade, com o pipeline de resiliência real e tempos curtos; o cofre é substituído por contatos sintéticos.
/// </summary>
public sealed class NotificationsApiFixture : IAsyncLifetime
{
    public static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(800);

    /// <summary>Partições isoladas do demo nestes testes; clínicas sem partição usam o circuito compartilhado.</summary>
    public const int IsolatedSlots = 4;

    private readonly MutableConfiguration _faults = new();
    private PostgresTestServer? _postgres;
    private ApplicationLogin? _login;
    private ApplicationLogin? _relay;
    private WebApplicationFactory<Program>? _factory;

    public CapturingLoggerProvider Logs { get; } = new();

    internal FakeContactDirectory Contacts { get; } = new();

    /// <summary>Par de chaves do código de confirmação (o serviço de direitos teria só a pública).</summary>
    internal ECDiffieHellman VerificationKey { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    internal StubVault Vault { get; } = new();

    public string AdminConnectionString =>
        _postgres?.AdminConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public string ApplicationConnectionString =>
        _login?.ConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public string RelayConnectionString =>
        _relay?.ConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    internal SimulatedProviderLedger Ledger => Services.GetRequiredService<SimulatedProviderLedger>();

    internal ChannelCircuits Circuits => Services.GetRequiredService<ChannelCircuits>();

    internal IServiceProvider Services => Factory.Services;

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _login = _postgres.NewApplicationLogin("notifications_api_test");
        _relay = _postgres.NewApplicationLogin("notifications_relay_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Notifications", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:NotificationsMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("ConnectionStrings:NotificationsRelay", _relay.ConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Messaging:Enabled", "false");
            builder.UseSetting("Notifications:DispatcherEnabled", "false");
            builder.UseSetting("Notifications:ConsentGracePeriod", "00:05:00");
            builder.UseSetting("Notifications:RoundRetryDelay", "00:00:00.010");
            builder.UseSetting("Notifications:MaxRoundRetryDelay", "00:00:00.050");
            builder.UseSetting("Notifications:MaxRounds", "3");
            builder.UseSetting("Notifications:Resilience:AttemptTimeout", "00:00:00.300");
            builder.UseSetting("Notifications:Resilience:TotalTimeout", "00:00:03");
            builder.UseSetting("Notifications:Resilience:MaxRetryAttempts", "2");
            builder.UseSetting("Notifications:Resilience:RetryBaseDelay", "00:00:00.010");
            builder.UseSetting("Notifications:Resilience:MinimumThroughput", "3");
            builder.UseSetting("Notifications:Resilience:FailureRatio", "0.5");
            builder.UseSetting("Notifications:Resilience:SamplingDuration", "00:00:30");
            builder.UseSetting("Notifications:Resilience:BreakDuration", BreakDuration.ToString("c"));
            builder.UseSetting("Vault:BaseAddress", "http://patient-vault.test/");
            builder.UseSetting("FaultInjection:ClinicOverridesEnabled", "true");
            builder.UseSetting("FaultInjection:IsolatedSlots", IsolatedSlots.ToString(CultureInfo.InvariantCulture));

            builder.UseSetting("ServiceTokens:Local:PrivateKey", Vault.PrivateKeyBase64);
            builder.UseSetting("IdentityVerification:PrivateKey", Convert.ToBase64String(VerificationKey.ExportPkcs8PrivateKey()));
            builder.UseSetting("Demo:SimulatedInboxEnabled", "true");
            builder.ConfigureAppConfiguration(configuration => configuration.Add(_faults));
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.AddTestAuthentication();
                services.RemoveAll<IContactDirectory>();
                services.AddSingleton<IContactDirectory>(Contacts);

                // O cliente real do cofre continua registrado: os testes do VaultContactDirectory o usam com o stub.
                services.AddHttpClient(VaultContactDirectory.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Vault)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
                services.PostConfigure<HttpStandardResilienceOptions>(VaultContactDirectory.ClientName + "-standard", options =>
                {
                    options.Retry.Delay = TimeSpan.FromMilliseconds(10);
                    options.Retry.UseJitter = false;
                });
            });
        });

        _ = _factory.Server;
        await _postgres.CreateLoginAsync(_login, DatabaseExtensions.ApplicationRole);
        await _postgres.CreateLoginAsync(_relay, DatabaseExtensions.RelayRole);
    }

    /// <summary>O diretório de contatos real, com o pipeline de resiliência configurado e o cofre simulado.</summary>
    internal VaultContactDirectory CreateVaultDirectory() =>
        ActivatorUtilities.CreateInstance<VaultContactDirectory>(
            Services, Services.GetRequiredService<IHttpClientFactory>().CreateClient(VaultContactDirectory.ClientName));

    public HttpClient CreateClient(Guid? tenantId, params string[] scopes)
    {
        var client = Factory.CreateClient();
        if (scopes.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, string.Join(' ', scopes));
        }

        if (tenantId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.Value.ToString());
        }

        return client;
    }

    /// <summary>Entrega um evento ao Notifications pelo mesmo processador de Inbox usado com o RabbitMQ.</summary>
    public async Task<ProcessOutcome> DeliverAsync<TEvent>(Guid tenantId, TEvent @event, Guid? messageId = null)
        where TEvent : IIntegrationEvent
    {
        var processor = Services.GetRequiredService<InboxProcessor<NotificationsDbContext>>();
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web);
        var context = new MessageContext(messageId ?? Guid.CreateVersion7(), tenantId, TEvent.EventType, DateTimeOffset.UtcNow);
        return await processor.ProcessAsync(context, body, TestContext.Current.CancellationToken);
    }

    public Task<ProcessOutcome> GrantNotificationsAsync(Guid tenantId, Guid patientRef, long version = 1) =>
        DeliverAsync(tenantId, new ConsentGranted(patientRef, ConsentPurposes.Notifications, "consent", "v1", version, DateTimeOffset.UtcNow));

    /// <summary>Roda ciclos do despachante até não haver aviso vencido. Devolve quantos foram processados.</summary>
    public Task<int> DispatchOnceAsync() =>
        Services.GetRequiredService<NotificationDispatcher>().RunOnceAsync(TestContext.Current.CancellationToken);

    public async Task<int> DispatchAsync()
    {
        var dispatcher = Services.GetRequiredService<NotificationDispatcher>();
        var total = 0;
        for (var cycle = 0; cycle < 20; cycle++)
        {
            var claimed = await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken);
            if (claimed == 0)
            {
                return total;
            }

            total += claimed;
        }

        return total;
    }

    /// <summary>Liga falhas por canal (ou desliga todas, sem argumentos).</summary>
    internal void InjectFaults(params (Channel Channel, FaultMode Mode)[] faults) => ConfigureFaults(faults.Length > 0, faults);

    /// <summary>Grava os modos por canal com a chave geral como pedido (para provar que desligada não injeta nada).</summary>
    internal void ConfigureFaults(bool enabled, params (Channel Channel, FaultMode Mode)[] faults)
    {
        var values = new Dictionary<string, string?> { ["FaultInjection:Enabled"] = enabled ? "true" : "false" };
        foreach (var (channel, mode) in faults)
        {
            values[$"FaultInjection:Channels:{channel.Slug()}:Mode"] = mode.ToString();
            values[$"FaultInjection:Channels:{channel.Slug()}:Latency"] = "00:00:01";
        }

        _faults.Set(values);
    }

    /// <summary>Desliga as falhas e fecha os circuitos, para cada teste começar com os canais saudáveis.</summary>
    internal async Task ResetChannelsAsync()
    {
        InjectFaults();

        // O dispatcher é global: jobs deixados por um teste anterior (rodadas pendentes) bateriam nos canais deste
        // teste e mexeriam no circuit breaker. Cada teste começa com a fila vazia.
        await ExecuteAsAdminAsync("DELETE FROM notification_jobs");

        await ExecuteAsAdminAsync("DELETE FROM clinic_channel_faults");
        await ExecuteAsAdminAsync("DELETE FROM clinic_partitions");

        // Fechar um circuito já fechado não zera as métricas de amostragem do Polly; isolar e fechar zera.
        var isolation = Services.GetRequiredService<ClinicIsolation>();
        foreach (var partition in isolation.IsolatedPartitions.Prepend(ClinicIsolation.Shared))
        {
            foreach (var channel in Enum.GetValues<Channel>())
            {
                var control = Circuits.ControlFor(channel, partition);
                await control.IsolateAsync(TestContext.Current.CancellationToken);
                await control.CloseAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    public Task<IReadOnlyList<OutboxRow>> OutboxAsync(Guid tenantId) =>
        OutboxRows.ReadAsync(AdminConnectionString, tenantId, TestContext.Current.CancellationToken);

    /// <summary>Executa SQL como admin (sem RLS), para preparar cenários como "o aviso foi criado há 10 minutos".</summary>
    public async Task<int> ExecuteAsAdminAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        Logs.Dispose();
        Vault.SigningKey.Dispose();
        VerificationKey.Dispose();
    }

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture não inicializada.");
}
