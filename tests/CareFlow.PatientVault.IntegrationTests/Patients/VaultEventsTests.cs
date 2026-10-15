using System.Net;

using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Npgsql;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>
/// O cofre publica pelo Outbox, na mesma transação: o cadastro (para a agenda) e cada acesso a dado pessoal
/// (para a auditoria). Nenhum evento carrega dado pessoal, só o pseudônimo.
/// </summary>
public sealed class VaultEventsTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private static string?[] Actions(IEnumerable<OutboxRow> rows) =>
        [.. rows.Where(row => row.AuditAction is not null).Select(row => row.AuditAction)];

    [Fact]
    public async Task Register_PublishesPatientRegisteredAndAudit()
    {
        // Arrange
        var client = api.CreateClient(_clinic, Write);

        // Act
        var (patientRef, _) = await client.RegisterOkAsync(NewPatient());

        // Assert
        var rows = await api.OutboxAsync(_clinic);
        var registered = Assert.Single(rows, row => row.Type == "patient.registered");
        Assert.Equal(patientRef, registered.Payload.GetProperty("patientRef").GetGuid());
        var audit = Assert.Single(rows, row => row.AuditAction == "patient.registered");
        Assert.Equal("patient-vault", audit.Payload.GetProperty("sourceService").GetString());
        Assert.Equal(patientRef, audit.Payload.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task Events_CarryNoPersonalData()
    {
        // Arrange
        var client = api.CreateClient(_clinic, Write, Read);
        var patient = NewPatient();
        var (patientRef, etag) = await client.RegisterOkAsync(patient);

        // Act
        await client.GetAsync($"/v1/patients/{patientRef}", Ct);
        await client.UpdateAsync(patientRef, new UpdatePatientRequest(patient.FullName + " Atualizado", patient.BirthDate, patient.Email, patient.Phone), etag);
        await client.LookupAsync(patient.Cpf);

        // Assert
        var payloads = (await api.OutboxAsync(_clinic)).Select(row => row.Payload.GetRawText()).ToArray();
        Assert.NotEmpty(payloads);
        foreach (var value in new[] { patient.FullName!, patient.Cpf!, patient.Email!, patient.Phone! })
        {
            Assert.DoesNotContain(payloads, payload => payload.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ReadUpdateLookup_EachRecordsAnAuditEvent()
    {
        // Arrange
        var client = api.CreateClient(_clinic, Write, Read);
        var patient = NewPatient();
        var (patientRef, etag) = await client.RegisterOkAsync(patient);

        // Act
        await client.GetAsync($"/v1/patients/{patientRef}", Ct);
        var update = await client.UpdateAsync(patientRef, new UpdatePatientRequest(patient.FullName, patient.BirthDate, patient.Email, patient.Phone), etag);
        await client.LookupAsync(patient.Cpf);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(
            ["patient.registered", "patient.read", "patient.updated", "patient.lookup"],
            Actions(await api.OutboxAsync(_clinic)));
    }

    [Fact]
    public async Task FailedOperations_PublishNothing()
    {
        // Arrange
        var client = api.CreateClient(_clinic, Write, Read);
        var patient = NewPatient();
        var (patientRef, _) = await client.RegisterOkAsync(patient);

        // Act
        var duplicate = await client.RegisterAsync(NewPatient(patient.Cpf));
        var stale = await client.UpdateAsync(patientRef, new UpdatePatientRequest(patient.FullName, patient.BirthDate, patient.Email, patient.Phone), "\"1\"");
        var missing = await client.LookupAsync(SyntheticPatients.Cpf());
        var foreign = await api.CreateClient(Guid.NewGuid(), Read).GetAsync($"/v1/patients/{patientRef}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var rows = await api.OutboxAsync(_clinic);
        Assert.Single(rows, row => row.Type == "patient.registered");
        Assert.Equal(["patient.registered"], Actions(rows));
    }

    [Fact]
    public async Task ApplicationLogin_CannotReadOutbox()
    {
        // Arrange
        await api.CreateClient(_clinic, Write).RegisterOkAsync(NewPatient());
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinic);

        // Act
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.CountAsync("SELECT count(*) FROM outbox_messages"));

        // Assert
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
