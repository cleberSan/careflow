using System.Net;

using CareFlow.Scheduling.Domain.Appointments;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

/// <summary>Toda reserva, leitura e cancelamento vai para a trilha de auditoria pelo Outbox, na mesma transação.</summary>
public sealed class AccessAuditTests(SchedulingApiFixture api)
{
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string?[]> AuditedActionsAsync(Guid tenant) =>
        [.. (await api.OutboxAsync(tenant)).Where(row => row.AuditAction is not null).Select(row => row.AuditAction)];

    [Fact]
    public async Task BookReadCancel_EachRecordsAnAuditEvent()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write, Read);
        var booked = await (await client.BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), Ct)).ReadAppointmentAsync();

        // Act
        await client.GetAsync($"/v1/appointments/{booked.Id}", Ct);
        var cancel = await client.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, Ct);

        // Assert
        Assert.Equal(CancellationReason.Requested, (await cancel.ReadAppointmentAsync()).CancellationReason);
        Assert.Equal(["appointment.booked", "appointment.read", "appointment.cancelled"], await AuditedActionsAsync(_tenant));
    }

    [Fact]
    public async Task Audit_CarriesTokenSubjectAndPatientPseudonym()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var request = Request(Guid.NewGuid(), FutureSlot());

        // Act
        await client.BookAsync(request, NewKey(), Ct);

        // Assert
        var payload = Assert.Single(await api.OutboxAsync(_tenant), row => row.AuditAction is not null).Payload;
        Assert.Equal("scheduling", payload.GetProperty("sourceService").GetString());
        Assert.Equal("patient", payload.GetProperty("resourceType").GetString());
        Assert.Equal(request.PatientRef, payload.GetProperty("resourceId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("actor").GetString()));
    }

    [Fact]
    public async Task FailedOrForeignAccess_RecordsNothing()
    {
        // Arrange
        var booked = await (await api.CreateClient(_tenant, Write).BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), Ct)).ReadAppointmentAsync();
        var otherClinic = Guid.NewGuid();
        var foreign = api.CreateClient(otherClinic, Write, Read);

        // Act
        var read = await foreign.GetAsync($"/v1/appointments/{booked.Id}", Ct);
        var cancel = await foreign.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Empty(await api.OutboxAsync(otherClinic));
        Assert.Equal(["appointment.booked"], await AuditedActionsAsync(_tenant));
    }
}
