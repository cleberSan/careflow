using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

[assembly: AssemblyFixture(typeof(SchedulingApiFixture))]

namespace CareFlow.Scheduling.IntegrationTests.Infrastructure;

/// <summary>
/// Um PostgreSQL real por execução e uma instância da API compartilhada. As migrations rodam com o papel
/// de admin e a API conecta com um login sem privilégios, como em produção, então o RLS vale nos testes.
/// Cada teste usa uma clínica nova, então os dados não se misturam sem precisar limpar o banco.
/// </summary>
public sealed class SchedulingApiFixture : IAsyncLifetime
{
    private PostgresTestServer? _postgres;
    private ApplicationLogin? _login;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>Connection string do login da API (sem superusuário, sujeito ao RLS).</summary>
    public string ApplicationConnectionString =>
        _login?.ConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public string AdminConnectionString =>
        _postgres?.AdminConnectionString ?? throw new InvalidOperationException("Fixture não inicializada.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresTestServer.StartAsync();
        _login = _postgres.NewApplicationLogin("scheduling_api_test");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Scheduling", _login.ConnectionString);
            builder.UseSetting("ConnectionStrings:SchedulingMigrations", _postgres.AdminConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.ConfigureTestServices(services => services.AddTestAuthentication());
        });

        // Sobe a aplicação (e aplica as migrations, que criam o papel da aplicação) uma única vez.
        // A API só conecta na primeira requisição, então o login pode ser criado logo depois.
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
    }

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture não inicializada.");
}
