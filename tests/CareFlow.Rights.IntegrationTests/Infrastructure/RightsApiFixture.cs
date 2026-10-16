using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Features.Exports;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(RightsApiFixture))]

namespace CareFlow.Rights.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real e uma instância do serviço de pedidos. Migrations com admin, API com login sem privilégios (RLS
/// vale), mensageria desligada (os eventos ficam no Outbox), limpeza de pacotes só quando o teste chama, e o par de
/// chaves do Notifications fica aqui para o teste abrir o código como o Notifications abriria.
/// </summary>
public sealed class RightsApiFixture : IAsyncLifetime
{
    private PostgresTestServer? _postgres;
    private ApplicationLogin? _login;
    private WebApplicationFactory<Program>? _factory;

    public CapturingLoggerProvider Logs { get; } = new();

    public ECDiffieHellman NotificationsKey { get; } = SealedBox.CreateKeyPair();

    public string AdminConnectionString =>
        _postgres?.AdminConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _login = _postgres.NewApplicationLogin("rights_api_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Rights", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:RightsMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Messaging:Enabled", "false");
            builder.UseSetting("Rights:SweepPackages", "false");
            builder.UseSetting("Rights:Keys:KeyEncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("IdentityVerification:NotificationsPublicKey", SealedBox.ExportPublicKey(NotificationsKey));
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

    public async Task<ProcessOutcome> DeliverAsync<TEvent>(Guid tenantId, TEvent @event, Guid? messageId = null)
        where TEvent : IIntegrationEvent
    {
        var processor = Factory.Services.GetRequiredService<InboxProcessor<RightsDbContext>>();
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web);
        var context = new MessageContext(messageId ?? Guid.CreateVersion7(), tenantId, TEvent.EventType, DateTimeOffset.UtcNow);
        return await processor.ProcessAsync(context, body, TestContext.Current.CancellationToken);
    }

    public Task<IReadOnlyList<OutboxRow>> OutboxAsync(Guid tenantId) =>
        OutboxRows.ReadAsync(AdminConnectionString, tenantId, TestContext.Current.CancellationToken);

    /// <summary>O código de confirmação do pedido, aberto com a chave do Notifications.</summary>
    public async Task<string> CodeForAsync(Guid tenantId, Guid requestId)
    {
        var rows = await OutboxAsync(tenantId);
        var row = rows.Single(r => r.Type == IdentityVerificationRequested.EventType && r.Payload.GetProperty("requestId").GetGuid() == requestId);
        var sealedCode = row.Payload.GetProperty("sealedCode").GetString()!;
        return Encoding.UTF8.GetString(SealedBox.Open(NotificationsKey, sealedCode, SubjectRequestSealing.VerificationCodeContext(requestId)));
    }

    public Task<int> SweepAsync() =>
        Factory.Services.GetRequiredService<PackageSweeper>().SweepAsync(TestContext.Current.CancellationToken);

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

        NotificationsKey.Dispose();
        Logs.Dispose();
    }

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture não inicializada.");
}
