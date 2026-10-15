using System.Net;

using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Patients;
using CareFlow.Scheduling.Api.Features.Patients;
using CareFlow.Scheduling.Domain.Appointments;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using Npgsql;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Patients;

/// <summary>
/// A agenda valida o paciente pela projeção alimentada por eventos (cadastro no cofre, consentimento no
/// Consent) e reage à revogação. Os eventos entram pelo mesmo processador de Inbox usado com o RabbitMQ.
/// </summary>
public sealed class PatientProjectionTests(SchedulingApiFixture api)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();
    private readonly Guid _professional = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Writer => api.CreateClientWithoutSeeding(_tenant, Write, Read);

    private Task<HttpResponseMessage> BookAsync(DateTimeOffset startsAt, Guid? patient = null, Guid? tenant = null) =>
        api.CreateClientWithoutSeeding(tenant ?? _tenant, Write)
            .BookAsync(Request(_professional, startsAt, patientRef: patient ?? _patient), NewKey(), Ct);

    private Task<ProcessOutcome> RegisterAsync(Guid? tenant = null) =>
        api.DeliverAsync(tenant ?? _tenant, new PatientRegistered(_patient, Now));

    private Task<ProcessOutcome> GrantAsync(long version, string purpose = ConsentPurposes.Scheduling, Guid? tenant = null, Guid? messageId = null) =>
        api.DeliverAsync(tenant ?? _tenant, new ConsentGranted(_patient, purpose, "consent", "v1", version, Now), messageId);

    private Task<ProcessOutcome> RevokeAsync(long version, string purpose = ConsentPurposes.Scheduling, Guid? tenant = null) =>
        api.DeliverAsync(tenant ?? _tenant, new ConsentRevoked(_patient, purpose, version, Now));

    [Fact]
    public async Task Book_UnknownPatient_ReturnsConflictNotYetKnown()
    {
        // Act
        var response = await BookAsync(FutureSlot());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Agenda.PacienteAindaNaoConhecido", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Book_RegisteredWithoutConsent_ReturnsConflictNoLegalBasis()
    {
        // Arrange
        await RegisterAsync();

        // Act
        var response = await BookAsync(FutureSlot());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Agenda.PacienteSemBaseLegal", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Book_ConsentWithoutRegistration_ReturnsConflictNotYetKnown()
    {
        // Arrange
        await GrantAsync(1);

        // Act
        var response = await BookAsync(FutureSlot());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Agenda.PacienteAindaNaoConhecido", await response.ReadProblemCodeAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Book_AfterRegistrationAndConsentInAnyOrder_Succeeds(bool registrationFirst)
    {
        // Arrange
        if (registrationFirst)
        {
            Assert.Equal(ProcessOutcome.Processed, await RegisterAsync());
            Assert.Equal(ProcessOutcome.Processed, await GrantAsync(1));
        }
        else
        {
            Assert.Equal(ProcessOutcome.Processed, await GrantAsync(1));
            Assert.Equal(ProcessOutcome.Processed, await RegisterAsync());
        }

        // Act
        var response = await BookAsync(FutureSlot());

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Deliver_SameMessageTwice_SecondIsDuplicate()
    {
        // Arrange
        var messageId = Guid.CreateVersion7();

        // Act
        var first = await GrantAsync(1, messageId: messageId);
        var second = await GrantAsync(1, messageId: messageId);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, first);
        Assert.Equal(ProcessOutcome.Duplicate, second);
    }

    [Fact]
    public async Task Deliver_OlderGrantAfterRevocation_KeepsPatientRevoked()
    {
        // Arrange: a revogação (versão 2) chega antes da concessão (versão 1) que a precedeu no Consent.
        await RegisterAsync();
        await RevokeAsync(2);

        // Act
        var lateGrant = await GrantAsync(1);
        var response = await BookAsync(FutureSlot());

        // Assert
        Assert.Equal(ProcessOutcome.Processed, lateGrant);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Agenda.PacienteSemBaseLegal", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Deliver_RegrantAfterRevocation_AllowsBookingAgain()
    {
        // Arrange
        await RegisterAsync();
        await GrantAsync(1);
        await RevokeAsync(2);

        // Act
        await GrantAsync(3);
        var response = await BookAsync(FutureSlot());

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Revocation_CancelsFutureAppointmentsAndBlocksNewOnes()
    {
        // Arrange
        await RegisterAsync();
        await GrantAsync(1);
        var first = await (await BookAsync(FutureSlot(hour: 9))).ReadAppointmentAsync();
        var second = await (await BookAsync(FutureSlot(hour: 10))).ReadAppointmentAsync();
        var otherPatient = Guid.NewGuid();
        await api.SeedPatientAsync(_tenant, otherPatient);
        var unrelated = await (await BookAsync(FutureSlot(hour: 11), otherPatient)).ReadAppointmentAsync();

        // Act
        var outcome = await RevokeAsync(2);
        var afterRevocation = await BookAsync(FutureSlot(hour: 12));

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var appointment = await (await Writer.GetAsync($"/v1/appointments/{id}", Ct)).ReadAppointmentAsync();
            Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
            Assert.Equal(CancellationReason.ConsentRevoked, appointment.CancellationReason);
        }

        var untouched = await (await Writer.GetAsync($"/v1/appointments/{unrelated.Id}", Ct)).ReadAppointmentAsync();
        Assert.Equal(AppointmentStatus.Booked, untouched.Status);
        Assert.Equal(HttpStatusCode.Conflict, afterRevocation.StatusCode);
        Assert.Equal("Agenda.PacienteSemBaseLegal", await afterRevocation.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Revocation_RecordsSystemCancellationsForAudit()
    {
        // Arrange
        await RegisterAsync();
        await GrantAsync(1);
        await BookAsync(FutureSlot(hour: 9));

        // Act
        await RevokeAsync(2);

        // Assert
        var entry = Assert.Single(await api.OutboxAsync(_tenant), row => row.AuditAction == ConsentRevokedConsumer.CancelAction);
        Assert.Equal("careflow-scheduling", entry.Payload.GetProperty("actor").GetString());
        Assert.Equal(_patient, entry.Payload.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task Revocation_OfAnotherPurpose_IsIgnored()
    {
        // Arrange
        await RegisterAsync();
        await GrantAsync(1);
        var booked = await (await BookAsync(FutureSlot())).ReadAppointmentAsync();

        // Act
        var outcome = await RevokeAsync(2, ConsentPurposes.Notifications);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var appointment = await (await Writer.GetAsync($"/v1/appointments/{booked.Id}", Ct)).ReadAppointmentAsync();
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(FutureSlot(hour: 14))).StatusCode);
    }

    [Fact]
    public async Task Revocation_InAnotherClinic_DoesNotAffectThisClinic()
    {
        // Arrange: o mesmo pseudônimo em duas clínicas (não acontece com o cofre, mas o isolamento não pode depender disso).
        var otherClinic = Guid.NewGuid();
        await RegisterAsync();
        await GrantAsync(1);
        await RegisterAsync(otherClinic);
        await GrantAsync(1, tenant: otherClinic);
        var booked = await (await BookAsync(FutureSlot())).ReadAppointmentAsync();

        // Act
        await RevokeAsync(2, tenant: otherClinic);

        // Assert
        var appointment = await (await Writer.GetAsync($"/v1/appointments/{booked.Id}", Ct)).ReadAppointmentAsync();
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(FutureSlot(hour: 15))).StatusCode);
        Assert.Equal("Agenda.PacienteSemBaseLegal", await (await BookAsync(FutureSlot(hour: 16), tenant: otherClinic)).ReadProblemCodeAsync());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Deliver_InvalidVersion_IsRejectedAsPoison(long version)
    {
        // Act
        var granted = await GrantAsync(version);
        var revoked = await RevokeAsync(version);

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, granted);
        Assert.Equal(ProcessOutcome.Invalid, revoked);
    }

    [Fact]
    public async Task Deliver_EmptyPatientRef_IsRejectedAsPoison()
    {
        // Act
        var outcome = await api.DeliverAsync(_tenant, new PatientRegistered(Guid.Empty, Now));

        // Assert
        Assert.Equal(ProcessOutcome.Invalid, outcome);
    }

    [Fact]
    public async Task Revocation_ConcurrentWithBookings_LeavesNoFutureBookedAppointment()
    {
        // Arrange
        await RegisterAsync();
        await GrantAsync(1);

        // Act: reservas e revogação ao mesmo tempo. A trava na projeção garante que nenhuma reserva sobrevive.
        var bookings = Enumerable.Range(0, 8).Select(i => BookAsync(FutureSlot(dayOffset: 40 + i))).ToList();
        var revocation = RevokeAsync(2);
        await Task.WhenAll([.. bookings, revocation]);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, await revocation);
        Assert.Equal(0, await CountBookedAsync());
    }

    [Fact]
    public async Task Revocation_DoesNotLogPatientRef()
    {
        // Arrange
        await RegisterAsync();
        await GrantAsync(1);
        await BookAsync(FutureSlot());

        // Act
        await RevokeAsync(2);

        // Assert
        var own = api.Logs.Entries
            .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal))
            .Select(entry => entry.OwnContent)
            .ToArray();
        Assert.Contains(own, entry => entry.Contains("Revogação de consentimento cancelou", StringComparison.Ordinal));
        foreach (var value in new[] { _patient.ToString(), _patient.ToString("N"), _tenant.ToString(), _tenant.ToString("N") })
        {
            Assert.DoesNotContain(own, entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<long> CountBookedAsync()
    {
        await using var connection = new NpgsqlConnection(api.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM appointments WHERE tenant_id = $1 AND patient_ref = $2 AND status = 'Booked'",
            connection)
        {
            Parameters = { new() { Value = _tenant }, new() { Value = _patient } },
        };
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
