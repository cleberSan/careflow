using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(SchedulingApiFixture))]

namespace CareFlow.Scheduling.IntegrationTests.Infrastructure;

/// <summary>
/// Um PostgreSQL real por execução (Testcontainers) e uma instância da API compartilhada.
/// Cada teste usa uma clínica nova, então os dados não se misturam sem precisar limpar o banco.
/// Defina CAREFLOW_TEST_POSTGRES para usar um banco já existente em vez do container.
/// </summary>
public sealed class SchedulingApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("CAREFLOW_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Scheduling", connectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.Configure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                });
            });
        });

        // Sobe a aplicação (e aplica as migrations) uma única vez.
        _ = _factory.Server;
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

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture não inicializada.");
}
