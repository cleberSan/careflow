using System.Text.Json;

using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts;
using CareFlow.Contracts.Consents;
using CareFlow.Testing;
using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Api.Models;
using CareFlow.Triage.Api.Profiles;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

using Npgsql;

[assembly: AssemblyFixture(typeof(TriageApiFixture))]

// O modelo programável e a captura de telemetria são do processo inteiro: os testes rodam em sequência.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace CareFlow.Triage.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real e uma instância do Triage. Migrations com admin, API e despachante com logins sem privilégios
/// (RLS vale), mensageria desligada: os testes entregam eventos pelo processador do Inbox. O provedor de IA é o
/// <see cref="ScriptedModel"/> (dentro do decorador real de prazo e telemetria) e o cofre é um
/// <see cref="FakeProfiles"/>; o cliente real do cofre continua registrado e é testado contra o <see cref="StubVault"/>.
/// </summary>
public sealed class TriageApiFixture : IAsyncLifetime
{
    public const int SubmissionsPerMinute = 40;

    public static readonly TimeSpan ModelTimeout = TimeSpan.FromMilliseconds(500);

    private PostgresTestServer? _postgres;
    private ApplicationLogin? _login;
    private ApplicationLogin? _relay;
    private WebApplicationFactory<Program>? _factory;

    public CapturingLoggerProvider Logs { get; } = new();

    internal TelemetryCapture Telemetry { get; } = new();

    internal ScriptedModel Model { get; } = new();

    internal FakeProfiles Profiles { get; } = new();

    internal StubVault Vault { get; } = new();

    public string AdminConnectionString =>
        _postgres?.AdminConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public string ApplicationConnectionString =>
        _login?.ConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    internal IServiceProvider Services => Factory.Services;

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _login = _postgres.NewApplicationLogin("triage_api_test");
        _relay = _postgres.NewApplicationLogin("triage_relay_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Triage", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:TriageMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("ConnectionStrings:TriageRelay", _relay.ConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Messaging:Enabled", "false");
            builder.UseSetting("Triage:SubmissionsPerClinicPerMinute", SubmissionsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Vault:BaseAddress", "http://patient-vault.test/");
            builder.UseSetting("ServiceTokens:Local:PrivateKey", Vault.PrivateKeyBase64);
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.AddTestAuthentication();
                services.RemoveAll<ITriageProfiles>();
                services.AddSingleton<ITriageProfiles>(Profiles);
                services.RemoveAll<ITriageModel>();
                services.AddSingleton<ITriageModel>(provider => new ObservedTriageModel(
                    Model,
                    ModelTimeout,
                    provider.GetRequiredService<TriageMetrics>(),
                    TimeProvider.System,
                    provider.GetRequiredService<ILogger<ObservedTriageModel>>()));

                services.AddHttpClient(VaultTriageProfiles.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Vault)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
                services.PostConfigure<HttpStandardResilienceOptions>(VaultTriageProfiles.ClientName + "-standard", options =>
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

    /// <summary>O cliente real do cofre, com o pipeline de resiliência configurado e o cofre simulado.</summary>
    internal VaultTriageProfiles CreateVaultProfiles() =>
        ActivatorUtilities.CreateInstance<VaultTriageProfiles>(
            Services, Services.GetRequiredService<IHttpClientFactory>().CreateClient(VaultTriageProfiles.ClientName));

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

    /// <summary>Cada teste começa com o modelo padrão e sem pedidos, logs ou spans de testes anteriores.</summary>
    internal void Reset()
    {
        Model.Reset();
        Telemetry.Clear();
    }

    /// <summary>Entrega um evento ao Triage pelo mesmo processador de Inbox usado com o RabbitMQ.</summary>
    public async Task<ProcessOutcome> DeliverAsync<TEvent>(Guid tenantId, TEvent @event, Guid? messageId = null)
        where TEvent : IIntegrationEvent
    {
        var processor = Services.GetRequiredService<InboxProcessor<TriageDbContext>>();
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web);
        var context = new MessageContext(messageId ?? Guid.CreateVersion7(), tenantId, TEvent.EventType, DateTimeOffset.UtcNow);
        return await processor.ProcessAsync(context, body, TestContext.Current.CancellationToken);
    }

    public Task<ProcessOutcome> GrantAiTriageAsync(Guid tenantId, Guid patientRef, long version = 1) =>
        DeliverAsync(tenantId, new ConsentGranted(patientRef, ConsentPurposes.AiTriage, "specific-consent", "2026-10", version, DateTimeOffset.UtcNow));

    public Task<ProcessOutcome> RevokeAiTriageAsync(Guid tenantId, Guid patientRef, long version = 2) =>
        DeliverAsync(tenantId, new ConsentRevoked(patientRef, ConsentPurposes.AiTriage, version, DateTimeOffset.UtcNow));

    /// <summary>Paciente sintético com consentimento ai-triage concedido.</summary>
    internal async Task<Guid> ConsentingPatientAsync(Guid tenantId, int age = 34, string? fullName = null)
    {
        var patientRef = Profiles.Add(age, fullName ?? SyntheticPatients.FullName());
        await GrantAiTriageAsync(tenantId, patientRef);
        return patientRef;
    }

    public Task<IReadOnlyList<OutboxRow>> OutboxAsync(Guid tenantId) =>
        OutboxRows.ReadAsync(AdminConnectionString, tenantId, TestContext.Current.CancellationToken);

    /// <summary>Lê uma coluna de texto do banco como admin (sem RLS).</summary>
    public async Task<string?> ScalarAsAdminAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString();
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

        Telemetry.Dispose();
        Logs.Dispose();
        Vault.SigningKey.Dispose();
    }

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture não inicializada.");
}
