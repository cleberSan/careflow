using System.Net;
using System.Net.Http.Json;

using CareFlow.Scheduling.Api.Features.Appointments;
using CareFlow.Scheduling.Domain.Appointments;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

public sealed class AppointmentLifecycleTests(SchedulingApiFixture api)
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _professional = Guid.NewGuid();

    private async Task<AppointmentResponse> BookAsync(HttpClient client, DateTimeOffset startsAt, int minutes = 30)
    {
        var response = await client.BookAsync(Request(_professional, startsAt, minutes), NewKey(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAppointmentAsync();
    }

    [Fact]
    public async Task Get_ExistingAppointment_ReturnsIt()
    {
        // Arrange
        var booked = await BookAsync(api.CreateClient(_tenant, Write), FutureSlot());
        var reader = api.CreateClient(_tenant, Read);

        // Act
        var response = await reader.GetAsync($"/v1/appointments/{booked.Id}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(booked, await response.ReadAppointmentAsync());
    }

    [Fact]
    public async Task Get_AppointmentFromAnotherClinic_ReturnsNotFound()
    {
        // Arrange
        var booked = await BookAsync(api.CreateClient(_tenant, Write), FutureSlot());
        var otherClinic = api.CreateClient(Guid.NewGuid(), Read);

        // Act
        var response = await otherClinic.GetAsync($"/v1/appointments/{booked.Id}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Agenda.ConsultaNaoEncontrada", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Cancel_ThenBookSameSlot_Succeeds()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var startsAt = FutureSlot();
        var booked = await BookAsync(client, startsAt);

        // Act
        var cancel = await client.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, TestContext.Current.CancellationToken);
        var rebook = await client.BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await cancel.ReadAppointmentAsync();
        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal(HttpStatusCode.Created, rebook.StatusCode);
    }

    [Fact]
    public async Task Cancel_Twice_ReturnsConflict()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var booked = await BookAsync(client, FutureSlot());
        await client.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, TestContext.Current.CancellationToken);

        // Act
        var response = await client.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Agenda.ConsultaJaCancelada", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Cancel_AppointmentFromAnotherClinic_ReturnsNotFound()
    {
        // Arrange
        var booked = await BookAsync(api.CreateClient(_tenant, Write), FutureSlot());
        var otherClinic = api.CreateClient(Guid.NewGuid(), Write);

        // Act
        var response = await otherClinic.PostAsync($"/v1/appointments/{booked.Id}/cancel", null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOnlyBookedSlotsInRange_Ordered()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var day = FutureSlot(hour: 0);
        var late = await BookAsync(client, day.AddHours(15));
        var early = await BookAsync(client, day.AddHours(9));
        var cancelled = await BookAsync(client, day.AddHours(11));
        await client.PostAsync($"/v1/appointments/{cancelled.Id}/cancel", null, TestContext.Current.CancellationToken);
        await BookAsync(client, day.AddDays(2).AddHours(9));

        // Act
        var slots = await client.GetFromJsonAsync<BusySlotResponse[]>(
            $"/v1/professionals/{_professional}/appointments?from={Uri.EscapeDataString(day.ToString("O"))}&to={Uri.EscapeDataString(day.AddDays(1).ToString("O"))}",
            Json,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(slots);
        Assert.Equal([early.Id, late.Id], slots.Select(s => s.AppointmentId));
    }

    [Fact]
    public async Task List_DoesNotExposePatient()
    {
        // Arrange
        var client = api.CreateClient(_tenant, Write);
        var startsAt = FutureSlot();
        await BookAsync(client, startsAt);

        // Act
        var body = await client.GetStringAsync(
            $"/v1/professionals/{_professional}/appointments?from={Uri.EscapeDataString(startsAt.ToString("O"))}&to={Uri.EscapeDataString(startsAt.AddHours(1).ToString("O"))}",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("patient", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("2030-01-02T00:00:00Z", "2030-01-01T00:00:00Z")]
    [InlineData("2030-01-01T00:00:00Z", "2030-01-01T00:00:00Z")]
    public async Task List_InvalidRange_ReturnsBadRequest(string from, string to)
    {
        // Act
        var response = await api.CreateClient(_tenant, Read).GetAsync(
            $"/v1/professionals/{_professional}/appointments?from={from}&to={to}",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_RangeLongerThanLimit_ReturnsBadRequest()
    {
        // Act
        var response = await api.CreateClient(_tenant, Read).GetAsync(
            $"/v1/professionals/{_professional}/appointments?from=2030-01-01T00:00:00Z&to=2030-03-01T00:00:00Z",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Agenda.PeriodoMuitoLongo", await response.ReadProblemCodeAsync());
    }
}
