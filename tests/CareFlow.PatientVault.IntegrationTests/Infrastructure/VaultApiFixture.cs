using System.Security.Cryptography;

using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(VaultApiFixture))]

namespace CareFlow.PatientVault.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL real e uma instância do cofre compartilhada. Migrations com o papel de admin, API com login
/// sem privilégios (RLS vale), mensageria desligada (eventos ficam no Outbox), KEK local aleatória por execução e todos os logs capturados em nível Trace.
/// Cada teste usa clínicas novas, então os dados não se misturam.
/// </summary>
public sealed class VaultApiFixture : IAsyncLifetime
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
        _login = _postgres.NewApplicationLogin("vault_api_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:PatientVault", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:PatientVaultMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Messaging:Enabled", "false");
            builder.UseSetting("KeyManagement:Provider", "Local");
            builder.UseSetting("KeyManagement:Local:ActiveKeyId", "test-1");
            builder.UseSetting("KeyManagement:Local:Keys:test-1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
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

    /// <summary>Eventos no Outbox da clínica, em ordem de criação (lido como admin; a mensageria fica desligada).</summary>
    public Task<IReadOnlyList<OutboxRow>> OutboxAsync(Guid tenantId) =>
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
