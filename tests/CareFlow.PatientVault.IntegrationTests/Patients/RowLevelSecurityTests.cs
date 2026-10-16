using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Npgsql;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>Isolamento garantido pelo PostgreSQL, sem depender do filtro do EF nem do código da API.</summary>
public sealed class RowLevelSecurityTests(VaultApiFixture api)
{
    private readonly Guid _clinicA = Guid.NewGuid();
    private readonly Guid _clinicB = Guid.NewGuid();

    private async Task<Guid> RegisterAsync(Guid clinic) =>
        (await api.CreateClient(clinic, Write).RegisterOkAsync(NewPatient())).PatientRef;

    [Theory]
    [InlineData("patients")]
    [InlineData("tenant_keys")]
    [InlineData("patient_keys")]
    public async Task WithoutClinicInSession_SeesNoRows(string table)
    {
        // Arrange
        await RegisterAsync(_clinicA);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, tenantId: null);

        // Act
        var visible = await connection.CountAsync($"SELECT count(*) FROM {table}");

        // Assert
        Assert.Equal(0, visible);
    }

    [Theory]
    [InlineData("patients")]
    [InlineData("tenant_keys")]
    [InlineData("patient_keys")]
    public async Task WithClinicInSession_SeesOnlyItsRows(string table)
    {
        // Arrange
        await RegisterAsync(_clinicA);
        await RegisterAsync(_clinicB);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var foreign = await connection.CountAsync($"SELECT count(*) FROM {table} WHERE tenant_id <> @a", ("a", _clinicA));
        var own = await connection.CountAsync($"SELECT count(*) FROM {table} WHERE tenant_id = @a", ("a", _clinicA));

        // Assert
        Assert.Equal(0, foreign);
        Assert.Equal(1, own);
    }

    [Fact]
    public async Task UpdateOfAnotherClinicsPatient_AffectsNothing()
    {
        // Arrange
        var patientOfB = await RegisterAsync(_clinicB);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var affected = await connection.ExecuteAsync(
            "UPDATE patients SET updated_at = now() WHERE patient_ref = @ref", ("ref", patientOfB));

        // Assert
        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task MovingOwnPatientToAnotherClinic_IsRejectedByPolicy()
    {
        // Arrange
        var patientOfA = await RegisterAsync(_clinicA);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var act = () => connection.ExecuteAsync(
            "UPDATE patients SET tenant_id = @b WHERE patient_ref = @ref", ("b", _clinicB), ("ref", patientOfA));

        // Assert
        var error = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Theory]
    [InlineData("DELETE FROM patients")]
    [InlineData("TRUNCATE patient_keys")]
    [InlineData("TRUNCATE tenant_keys")]
    [InlineData("UPDATE tenant_keys SET kek_id = 'x'")]
    [InlineData("ALTER TABLE patients DISABLE ROW LEVEL SECURITY")]
    public async Task ApplicationLogin_LacksDestructivePrivileges(string sql)
    {
        // Arrange
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var act = () => connection.ExecuteAsync(sql);

        // Assert
        var error = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task DeletingClinicKeyStillInUse_IsRejectedByForeignKey()
    {
        // Arrange: a rotação precisa de DELETE em tenant_keys, mas uma versão que ainda protege dados não sai.
        await RegisterAsync(_clinicA);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var act = () => connection.ExecuteAsync("DELETE FROM tenant_keys");

        // Assert
        var error = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task DeletingAnotherClinicsPatientKey_AffectsNothing()
    {
        // Arrange
        var patientOfB = await RegisterAsync(_clinicB);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var affected = await connection.ExecuteAsync("DELETE FROM patient_keys WHERE patient_ref = @ref", ("ref", patientOfB));

        // Assert
        Assert.Equal(0, affected);
    }
}
