using System.Text.Json;

using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

[assembly: AssemblyFixture(typeof(SchedulingApiFixture))]

namespace CareFlow.Scheduling.IntegrationTests.Infrastructure;

/// <summary>
/// Um PostgreSQL real por execução e uma instância da API compartilhada. As migrations rodam com o papel
/// de admin e a API conecta com um login sem privilégios, como em produção, então o RLS vale nos testes.
/// Cada teste usa uma clínica nova, então os dados não se misturam sem precisar limpar o banco.
/// A mensageria fica desligada: os eventos publicados ficam no Outbox, onde os testes os conferem, e os
/// consumidores são chamados direto pelo processador do Inbox com <see cref="DeliverAsync"/>.
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

    public CapturingLoggerProvider Logs { get; } = new();

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
            builder.UseSetting("Messaging:Enabled", "false");
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services => services.AddTestAuthentication());
        });

        // Sobe a aplicação (e aplica as migrations, que criam o papel da aplicação) uma única vez.
        // A API só conecta na primeira requisição, então o login pode ser criado logo depois.
        _ = _factory.Server;
        await _postgres.CreateLoginAsync(_login, DatabaseExtensions.ApplicationRole);
    }

    /// <summary>
    /// Cliente cujas reservas encontram o paciente já cadastrado e com consentimento para agendamento: antes
    /// de cada POST de reserva, o handler grava a projeção como os eventos gravariam. É o caminho comum dos
    /// testes que não tratam de elegibilidade.
    /// </summary>
    public HttpClient CreateClient(Guid? tenantId, params string[] scopes) =>
        Configure(Factory.CreateDefaultClient(new EligiblePatientsHandler(this, tenantId)), tenantId, scopes);

    /// <summary>Cliente sem atalho: a reserva depende do que a projeção realmente tem.</summary>
    public HttpClient CreateClientWithoutSeeding(Guid? tenantId, params string[] scopes) =>
        Configure(Factory.CreateClient(), tenantId, scopes);

    /// <summary>Entrega um evento ao consumidor da agenda, pelo mesmo processador de Inbox do RabbitMQ.</summary>
    public async Task<ProcessOutcome> DeliverAsync<TEvent>(Guid tenantId, TEvent @event, Guid? messageId = null)
        where TEvent : IIntegrationEvent
    {
        var processor = Factory.Services.GetRequiredService<InboxProcessor<SchedulingDbContext>>();
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, JsonSerializerOptions.Web);
        var context = new MessageContext(messageId ?? Guid.CreateVersion7(), tenantId, TEvent.EventType, DateTimeOffset.UtcNow);
        return await processor.ProcessAsync(context, body, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Grava a projeção do paciente direto no banco, como admin (ignora RLS). Com <paramref name="overwrite"/>
    /// falso, não mexe numa linha que já exista (o que os eventos gravaram continua valendo).
    /// </summary>
    public async Task SeedPatientAsync(
        Guid tenantId, Guid patientRef, bool registered = true, string consent = "Granted", long version = 1, bool overwrite = true)
    {
        var onConflict = overwrite
            ? """
              DO UPDATE SET is_registered = EXCLUDED.is_registered,
                  scheduling_consent = EXCLUDED.scheduling_consent,
                  consent_version = EXCLUDED.consent_version
              """
            : "DO NOTHING";

        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO patient_statuses (tenant_id, patient_ref, is_registered, scheduling_consent, consent_version, updated_at)
            VALUES ($1, $2, $3, $4, $5, now())
            ON CONFLICT (tenant_id, patient_ref) {onConflict}
            """,
            connection)
        {
            Parameters =
            {
                new() { Value = tenantId },
                new() { Value = patientRef },
                new() { Value = registered },
                new() { Value = consent },
                new() { Value = version },
            },
        };
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Eventos no Outbox da clínica, em ordem de criação (lido como admin).</summary>
    public Task<IReadOnlyList<OutboxRow>> OutboxAsync(Guid tenantId) =>
        OutboxRows.ReadAsync(AdminConnectionString, tenantId, TestContext.Current.CancellationToken);

    private static HttpClient Configure(HttpClient client, Guid? tenantId, string[] scopes)
    {
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
