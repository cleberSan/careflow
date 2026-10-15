using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Patients;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Npgsql;

namespace CareFlow.Scheduling.IntegrationTests.Patients;

/// <summary>RLS nas tabelas novas da agenda: projeção de pacientes, Outbox e Inbox.</summary>
public sealed class MessagingIsolationTests(SchedulingApiFixture api)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Guid _clinicA = Guid.NewGuid();
    private readonly Guid _clinicB = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    [Fact]
    public async Task ApplicationLogin_SeesOnlyItsClinicsProjectionAndInbox()
    {
        // Arrange
        await api.DeliverAsync(_clinicA, new PatientRegistered(_patient, Now));
        await api.DeliverAsync(_clinicA, new ConsentGranted(_patient, ConsentPurposes.Scheduling, "consent", "v1", 1, Now));
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicB);

        // Act
        var projection = await connection.CountAsync("SELECT count(*) FROM patient_statuses WHERE patient_ref = @p", ("p", _patient));
        var inbox = await connection.CountAsync("SELECT count(*) FROM inbox_messages WHERE tenant_id = @t", ("t", _clinicA));

        // Assert
        Assert.Equal(0, projection);
        Assert.Equal(0, inbox);
    }

    [Fact]
    public async Task ApplicationLogin_CannotReadOutbox()
    {
        // Arrange
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.CountAsync("SELECT count(*) FROM outbox_messages"));

        // Assert: a API só insere no Outbox; ler eventos (de qualquer clínica) é do despachante.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task ApplicationLogin_CannotWriteProjectionOfAnotherClinic()
    {
        // Arrange
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO patient_statuses (tenant_id, patient_ref, is_registered, scheduling_consent, consent_version, updated_at)
            VALUES (@other, @p, true, 'Granted', 99, now())
            """,
            ("other", _clinicB),
            ("p", _patient)));

        // Assert
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task ApplicationLogin_CannotDeleteInboxOrProjection()
    {
        // Arrange
        await api.DeliverAsync(_clinicA, new PatientRegistered(_patient, Now));
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var inbox = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("DELETE FROM inbox_messages"));
        var projection = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("DELETE FROM patient_statuses"));

        // Assert: apagar o Inbox reabriria a porta para reprocessar eventos; é tarefa só do despachante.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, inbox.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, projection.SqlState);
    }
}
