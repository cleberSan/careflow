using System.Net;

using CareFlow.Consent.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.Consent.IntegrationTests.Infrastructure.ConsentClient;

namespace CareFlow.Consent.IntegrationTests.Consents;

/// <summary>Isolamento entre clínicas em cada endpoint do Consent, e o que o banco garante sozinho.</summary>
public sealed class ClinicIsolationTests(ConsentApiFixture api)
{
    private readonly Guid _clinicA = Guid.NewGuid();
    private readonly Guid _clinicB = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    [Fact]
    public async Task List_DoesNotShowAnotherClinicsConsents()
    {
        // Arrange
        await api.CreateClient(_clinicA, Write).GrantAsync(_patient, "scheduling", Specific());

        // Act
        var consents = await api.CreateClient(_clinicB, Read).ListOkAsync(_patient);

        // Assert
        Assert.Empty(consents);
    }

    [Fact]
    public async Task Revoke_AnotherClinicsConsent_ReturnsNotFoundAndKeepsIt()
    {
        // Arrange
        await api.CreateClient(_clinicA, Write).GrantAsync(_patient, "scheduling", Specific());

        // Act
        var response = await api.CreateClient(_clinicB, Write).RevokeAsync(_patient, "scheduling");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stillGranted = await api.CreateClient(_clinicA, Read).ListOkAsync(_patient);
        Assert.Equal("granted", Assert.Single(stillGranted).Status);
    }

    [Fact]
    public async Task Grant_SamePseudonymInAnotherClinic_CreatesSeparateRecord()
    {
        // Arrange
        await api.CreateClient(_clinicA, Write).GrantAsync(_patient, "scheduling", Specific());

        // Act
        var response = await api.CreateClient(_clinicB, Write).GrantAsync(_patient, "scheduling", Specific());

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ApplicationLogin_CannotReadOutboxOrOtherClinics()
    {
        // Arrange
        await api.CreateClient(_clinicA, Write).GrantAsync(_patient, "scheduling", Specific());
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicB);

        // Act
        var visible = await connection.CountAsync("SELECT count(*) FROM consents WHERE patient_ref = @p", ("p", _patient));
        var outbox = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => connection.CountAsync("SELECT count(*) FROM outbox_messages"));

        // Assert: a API só insere no Outbox; ler eventos (de qualquer clínica) é do despachante.
        Assert.Equal(0, visible);
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, outbox.SqlState);
    }

    [Fact]
    public async Task ApplicationLogin_CannotDeleteConsents()
    {
        // Arrange
        await api.CreateClient(_clinicA, Write).GrantAsync(_patient, "scheduling", Specific());
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act & Assert
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            connection.ExecuteAsync("DELETE FROM consents WHERE patient_ref = @p", ("p", _patient)));
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task ApplicationLogin_CannotEnqueueEventForAnotherClinic()
    {
        // Arrange
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act & Assert
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => connection.ExecuteAsync(
            """
            INSERT INTO outbox_messages (id, tenant_id, type, payload, occurred_at, attempts, next_attempt_at)
            VALUES (gen_random_uuid(), @other, 'consent.revoked', '{}', now(), 0, now())
            """,
            ("other", _clinicB)));
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
