using System.Text.Json;

using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Consent.IntegrationTests.Infrastructure;
using CareFlow.Contracts;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(ConsentApiFixture))]

namespace CareFlow.Consent.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real e uma instância do Consent compartilhada. Migrations com admin, API com login sem
/// privilégios (RLS vale), mensageria desligada (os eventos ficam no Outbox, onde os testes os conferem)
/// e todos os logs capturados em nível Trace.
/// </summary>
public sealed class ConsentApiFixture : IAsyncLifetime
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
        _login = _postgres.NewApplicationLogin("consent_api_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Consent", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:ConsentMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Messaging:Enabled", "false");
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services => services.AddTestAuthentication());
        });

        _ = _factory.Server;
        await _postgres.CreateLoginAsync(_login, DatabaseExtensions.ApplicationRole);
    }

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

    /// <summary>Entrega um evento pelo processador do Inbox, como faria o consumidor do RabbitMQ.</summary>
    public async Task<ProcessOutcome> DeliverAsync<TEvent>(Guid tenantId, TEvent @event, Guid? messageId = null)
        where TEvent : IIntegrationEvent
    {
        var processor = Factory.Services.GetRequiredService<InboxProcessor<ConsentDbContext>>();
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web);
        var context = new MessageContext(messageId ?? Guid.CreateVersion7(), tenantId, TEvent.EventType, DateTimeOffset.UtcNow);
        return await processor.ProcessAsync(context, body, TestContext.Current.CancellationToken);
    }

    /// <summary>Eventos no Outbox da clínica, em ordem de criação (lido como admin).</summary>
    public Task<IReadOnlyList<OutboxRow>> TenantOutboxAsync(Guid tenantId) =>
        OutboxRows.ReadAsync(AdminConnectionString, tenantId, TestContext.Current.CancellationToken);

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
