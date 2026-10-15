using System.Net;

using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

/// <summary>
/// Prova o isolamento no próprio banco, sem passar pelo filtro do EF: é o que protege as clínicas
/// mesmo que a aplicação esqueça um filtro ou receba um id de outra clínica.
/// </summary>
public sealed class RowLevelSecurityTests(SchedulingApiFixture api)
{
    private const string CountByTenant = "SELECT count(*) FROM appointments WHERE tenant_id = @tenant";

    private readonly Guid _clinicA = Guid.NewGuid();
    private readonly Guid _clinicB = Guid.NewGuid();

    private async Task<Guid> BookAsync(Guid tenant)
    {
        var response = await api.CreateClient(tenant, Write)
            .BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAppointmentAsync()).Id;
    }

    [Fact]
    public async Task WithoutClinicInSession_SeesNoRows()
    {
        // Arrange
        await BookAsync(_clinicA);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, tenantId: null);

        // Act
        var visible = await connection.CountAsync("SELECT count(*) FROM appointments");

        // Assert
        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task WithClinicInSession_SeesOnlyItsRows_EvenWithoutWhereClause()
    {
        // Arrange
        await BookAsync(_clinicA);
        await BookAsync(_clinicB);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var ownRows = await connection.CountAsync(CountByTenant, ("tenant", _clinicA));
        var otherRows = await connection.CountAsync(CountByTenant, ("tenant", _clinicB));
        var foreignTenants = await connection.CountAsync("SELECT count(*) FROM appointments WHERE tenant_id <> @tenant", ("tenant", _clinicA));

        // Assert
        Assert.Equal(1, ownRows);
        Assert.Equal(0, otherRows);
        Assert.Equal(0, foreignTenants);
    }

    [Fact]
    public async Task UpdateOfAnotherClinicsRow_AffectsNothing()
    {
        // Arrange
        var appointmentOfB = await BookAsync(_clinicB);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var affected = await connection.ExecuteAsync(
            "UPDATE appointments SET status = 'Cancelled' WHERE id = @id", ("id", appointmentOfB));

        // Assert
        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task InsertForAnotherClinic_IsRejectedByPolicy()
    {
        // Arrange
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);
        var startsAt = FutureSlot();

        // Act
        var act = () => connection.ExecuteAsync(
            """
            INSERT INTO appointments (id, tenant_id, professional_id, patient_ref, starts_at, ends_at, status,
                                      created_at, idempotency_key, request_fingerprint)
            VALUES (@id, @tenant, @id, @id, @starts, @ends, 'Booked', now(), 'k', repeat('0', 64))
            """,
            ("id", Guid.NewGuid()),
            ("tenant", _clinicB),
            ("starts", startsAt),
            ("ends", startsAt.AddMinutes(30)));

        // Assert
        var error = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task ApplicationLogin_CannotDelete()
    {
        // Arrange
        var appointment = await BookAsync(_clinicA);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinicA);

        // Act
        var act = () => connection.ExecuteAsync("DELETE FROM appointments WHERE id = @id", ("id", appointment));

        // Assert
        var error = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task HealthCheck_WithRoleThatBypassesRls_IsUnhealthy()
    {
        // Arrange: o admin dos testes é superusuário, como seria uma connection string errada em produção.
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseNpgsql(api.AdminConnectionString).Options;
        await using var db = new SchedulingDbContext(options, NoTenantContext.Instance);
        var check = new RowLevelSecurityHealthCheck<SchedulingDbContext>(db);

        // Act
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
