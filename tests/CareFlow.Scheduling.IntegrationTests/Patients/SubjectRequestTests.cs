using System.Net;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.Contracts.Appointments;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Patients;
using CareFlow.Contracts.Rights;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Patients;

/// <summary>A agenda respondendo a pedidos do titular: exportação das consultas e eliminação (ADR-0012).</summary>
public sealed class SubjectRequestTests(SchedulingApiFixture api)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();
    private readonly Guid _professional = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> BookAsync(DateTimeOffset startsAt)
    {
        var response = await api.CreateClientWithoutSeeding(_tenant, Write)
            .BookAsync(Request(_professional, startsAt, patientRef: _patient), NewKey(), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAppointmentAsync()).Id;
    }

    private async Task ArrangePatientAsync()
    {
        await api.DeliverAsync(_tenant, new PatientRegistered(_patient, Now));
        await api.DeliverAsync(_tenant, new ConsentGranted(_patient, ConsentPurposes.Scheduling, "consent", "v1", 1, Now));
    }

    /// <summary>Leva uma consulta para o passado direto no banco (a API não reserva no passado).</summary>
    private Task<int> MoveToPastAsync(Guid appointmentId) =>
        AdminSql.ExecuteAsync(
            api.AdminConnectionString,
            "UPDATE appointments SET starts_at = now() - interval '10 days', ends_at = now() - interval '10 days' + interval '30 minutes' WHERE id = @id",
            ("id", appointmentId));

    private SubjectRequestOpened Erasure() =>
        new(Guid.NewGuid(), _patient, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, Now);

    [Fact]
    public async Task Export_ListsEveryAppointment_SealedForTheRequest()
    {
        // Arrange
        await ArrangePatientAsync();
        var future = await BookAsync(FutureSlot(10));
        var past = await BookAsync(FutureSlot(11));
        await MoveToPastAsync(past);
        using var requestKey = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), _patient, SubjectRequestKinds.Portability, SealedBox.ExportPublicKey(requestKey), RetentionPolicy.Version, Now);

        // Act
        await api.DeliverAsync(_tenant, opened);

        // Assert
        var answer = (await api.OutboxAsync(_tenant)).AnswerTo(opened.RequestId);
        Assert.Equal(SubjectRequestOutcomes.Exported, answer.GetProperty("outcome").GetString());
        var section = SealedBox.Open(
            requestKey, answer.GetProperty("sealedSection").GetString()!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Scheduling));
        using var json = JsonDocument.Parse(section);
        var ids = json.RootElement.GetProperty("appointments").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([past, future], ids);
    }

    [Fact]
    public async Task Erasure_CancelsFutureAppointments_KeepsPastOnes_AndBlocksNewBookings()
    {
        // Arrange
        await ArrangePatientAsync();
        var future = await BookAsync(FutureSlot(12));
        var past = await BookAsync(FutureSlot(13));
        await MoveToPastAsync(past);
        var opened = Erasure();

        // Act
        await api.DeliverAsync(_tenant, opened);

        // Assert
        Assert.Equal("Cancelled", await AdminSql.ScalarAsync<string>(api.AdminConnectionString, "SELECT status FROM appointments WHERE id = @id", ("id", future)));
        Assert.Equal("SubjectErasure", await AdminSql.ScalarAsync<string>(api.AdminConnectionString, "SELECT cancellation_reason FROM appointments WHERE id = @id", ("id", future)));
        Assert.Equal("Booked", await AdminSql.ScalarAsync<string>(api.AdminConnectionString, "SELECT status FROM appointments WHERE id = @id", ("id", past)));

        var outbox = await api.OutboxAsync(_tenant);
        var cancelled = Assert.Single(outbox, row => row.Type == AppointmentCancelled.EventType);
        Assert.Equal(AppointmentCancellationReasons.SubjectErasure, cancelled.Payload.GetProperty("reason").GetString());

        var answer = outbox.AnswerTo(opened.RequestId);
        var lines = answer.GetProperty("erasure").Deserialize<List<ErasureLine>>(Json)!;
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.SchedulingFutureAppointments, RetentionActions.Cancelled, 1), lines);
        Assert.Contains(new ErasureLine(RetentionPolicy.Items.SchedulingPastAppointments, RetentionActions.Retained, 1), lines);

        var refused = await api.CreateClientWithoutSeeding(_tenant, Write).BookAsync(Request(_professional, FutureSlot(14), patientRef: _patient), NewKey(), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Agenda.PacienteEliminado", await refused.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Erasure_IsFinal_LaterConsentEventsDoNotReopenBooking()
    {
        // Arrange
        await ArrangePatientAsync();
        await api.DeliverAsync(_tenant, Erasure());

        // Act: uma concessão atrasada chega depois da eliminação.
        await api.DeliverAsync(_tenant, new ConsentGranted(_patient, ConsentPurposes.Scheduling, "consent", "v1", 5, Now));
        await api.DeliverAsync(_tenant, new ErasureReapplied(Guid.NewGuid(), _patient, RetentionPolicy.Version, Now));
        var refused = await api.CreateClientWithoutSeeding(_tenant, Write).BookAsync(Request(_professional, FutureSlot(15), patientRef: _patient), NewKey(), Ct);

        // Assert
        Assert.Equal("Agenda.PacienteEliminado", await refused.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task RequestFromAnotherClinic_NeverTouchesItsAppointments()
    {
        // Arrange
        await ArrangePatientAsync();
        var appointment = await BookAsync(FutureSlot(16));
        var otherClinic = Guid.NewGuid();

        // Act: mesmo pseudônimo, outra clínica.
        await api.DeliverAsync(otherClinic, Erasure());

        // Assert
        Assert.Equal("Booked", await AdminSql.ScalarAsync<string>(api.AdminConnectionString, "SELECT status FROM appointments WHERE id = @id", ("id", appointment)));
        Assert.DoesNotContain(await api.OutboxAsync(otherClinic), row => row.Type == AppointmentCancelled.EventType);
        var answer = (await api.OutboxAsync(otherClinic)).Single(row => row.Type == SubjectRequestAnswered.EventType).Payload;
        var lines = answer.GetProperty("erasure").Deserialize<List<ErasureLine>>(Json)!;
        Assert.All(lines, line => Assert.Equal(0, line.Records));
    }
}
