using System.Security.Cryptography;
using System.Text.Json;

using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.Api.Infrastructure.Persistence;
using CareFlow.Audit.Domain.Chain;
using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Contracts;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(AuditApiFixture))]

namespace CareFlow.Audit.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real e uma instância da auditoria compartilhada. Migrations com admin, API com login sem
/// privilégios (RLS vale), chave da cadeia aleatória por execução e todos os logs capturados em nível Trace.
/// </summary>
public sealed class AuditApiFixture : IAsyncLifetime
{
    private PostgresTestServer? _postgres;
    private ApplicationLogin? _login;
    private WebApplicationFactory<Program>? _factory;

    public CapturingLoggerProvider Logs { get; } = new();

    public string ApplicationConnectionString =>
        _login?.ConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public string AdminConnectionString =>
        _postgres?.AdminConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _login = _postgres.NewApplicationLogin("audit_api_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Audit", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:AuditMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Messaging:Enabled", "false");
            builder.UseSetting("Audit:Chain:ActiveKeyId", "test-1");
            builder.UseSetting("Audit:Chain:Keys:test-1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services => services.AddTestAuthentication());
        });

        _ = _factory.Server;
        await _postgres.CreateLoginAsync(_login, DatabaseExtensions.ApplicationRole);
    }

    /// <summary>Grava um evento como o consumidor de mensagens fará: escopo próprio com a clínica do evento.</summary>
    internal Task<AppendOutcome> AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return AppendWithScopeAsync(auditEvent.TenantId, auditEvent, cancellationToken);
    }

    internal async Task<AppendOutcome> AppendWithScopeAsync(Guid scopeTenant, AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Enter(scopeTenant);
        return await scope.ServiceProvider.GetRequiredService<AuditWriter>().AppendAsync(auditEvent, cancellationToken);
    }

    /// <summary>Entrega um evento ao consumidor da auditoria, pelo mesmo processador de Inbox usado com o RabbitMQ.</summary>
    public async Task<ProcessOutcome> DeliverAsync<TEvent>(Guid tenantId, TEvent @event, Guid messageId)
        where TEvent : IIntegrationEvent
    {
        var processor = Factory.Services.GetRequiredService<InboxProcessor<AuditDbContext>>();
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web);
        var context = new MessageContext(messageId, tenantId, TEvent.EventType, DateTimeOffset.UtcNow);
        return await processor.ProcessAsync(context, body, TestContext.Current.CancellationToken);
    }

    /// <summary>Eventos que a auditoria publicou para a clínica (respostas a pedidos do titular).</summary>
    public Task<IReadOnlyList<OutboxRow>> OutboxAsync(Guid tenantId) =>
        OutboxRows.ReadAsync(AdminConnectionString, tenantId, TestContext.Current.CancellationToken);

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

    public HttpClient CreateAnonymousClient() => Factory.CreateClient();

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
    }

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture não inicializada.");
}
