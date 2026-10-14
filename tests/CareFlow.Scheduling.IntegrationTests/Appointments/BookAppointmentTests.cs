using System.Net;

using CareFlow.Scheduling.Domain.Appointments;
using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

public sealed class BookAppointmentTests(SchedulingApiFixture api)
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _professional = Guid.NewGuid();

    private HttpClient Writer() => api.CreateClient(_tenant, Write);

    [Fact]
    public async Task Book_ValidRequest_ReturnsCreatedWithLocation()
    {
        // Arrange
        var startsAt = FutureSlot();
        var request = Request(_professional, startsAt);

        // Act
        var response = await Writer().BookAsync(request, NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var appointment = await response.ReadAppointmentAsync();
        Assert.Equal($"/v1/appointments/{appointment.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(_professional, appointment.ProfessionalId);
        Assert.Equal(request.PatientRef, appointment.PatientRef);
        Assert.Equal(startsAt, appointment.StartsAt);
        Assert.Equal(AppointmentStatus.Booked, appointment.Status);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(15, 30)]
    [InlineData(-15, 30)]
    [InlineData(5, 10)]
    [InlineData(-30, 120)]
    public async Task Book_OverlappingSlot_ReturnsConflict(int offsetMinutes, int durationMinutes)
    {
        // Arrange
        var startsAt = FutureSlot();
        var client = Writer();
        await client.BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken);

        // Act
        var response = await client.BookAsync(
            Request(_professional, startsAt.AddMinutes(offsetMinutes), durationMinutes),
            NewKey(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Agenda.HorarioIndisponivel", await response.ReadProblemCodeAsync());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    public async Task Book_AdjacentSlot_Succeeds(int offsetMinutes)
    {
        // Arrange: intervalos [início, fim) que só se tocam na borda não conflitam.
        var startsAt = FutureSlot();
        var client = Writer();
        await client.BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken);

        // Act
        var response = await client.BookAsync(
            Request(_professional, startsAt.AddMinutes(offsetMinutes)),
            NewKey(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Book_SameTimeForAnotherProfessional_Succeeds()
    {
        // Arrange
        var startsAt = FutureSlot();
        var client = Writer();
        await client.BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken);

        // Act
        var response = await client.BookAsync(Request(Guid.NewGuid(), startsAt), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Book_SameProfessionalAndTimeInAnotherClinic_Succeeds()
    {
        // Arrange: o identificador do profissional é escopado por clínica.
        var startsAt = FutureSlot();
        await Writer().BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken);
        var otherClinic = api.CreateClient(Guid.NewGuid(), Write);

        // Act
        var response = await otherClinic.BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Book_ConcurrentRequestsForSameSlot_ExactlyOneSucceeds()
    {
        // Arrange
        const int attempts = 20;
        var startsAt = FutureSlot();
        var client = Writer();

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            client.BookAsync(Request(_professional, startsAt), NewKey(), TestContext.Current.CancellationToken)));

        // Assert
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(attempts - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Book_RetryWithSameKeyAndPayload_ReplaysOriginalAppointment()
    {
        // Arrange
        var client = Writer();
        var key = NewKey();
        var request = Request(_professional, FutureSlot());
        var first = await client.BookAsync(request, key, TestContext.Current.CancellationToken);

        // Act
        var retry = await client.BookAsync(request, key, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await first.ReadAppointmentAsync()).Id, (await retry.ReadAppointmentAsync()).Id);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
    }

    [Fact]
    public async Task Book_ConcurrentRetriesWithSameKey_AllReturnSameAppointment()
    {
        // Arrange
        var client = Writer();
        var key = NewKey();
        var request = Request(_professional, FutureSlot());

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            client.BookAsync(request, key, TestContext.Current.CancellationToken)));

        // Assert
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(r => r.ReadAppointmentAsync()));
        Assert.Single(ids.Select(a => a.Id).Distinct());
    }

    [Fact]
    public async Task Book_SameKeyWithDifferentPayload_ReturnsConflict()
    {
        // Arrange
        var client = Writer();
        var key = NewKey();
        await client.BookAsync(Request(_professional, FutureSlot()), key, TestContext.Current.CancellationToken);

        // Act
        var response = await client.BookAsync(Request(_professional, FutureSlot(hour: 14)), key, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Idempotencia.ChaveReutilizada", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Book_SameKeyInAnotherClinic_IsIndependent()
    {
        // Arrange
        var key = NewKey();
        await Writer().BookAsync(Request(_professional, FutureSlot()), key, TestContext.Current.CancellationToken);
        var otherClinic = api.CreateClient(Guid.NewGuid(), Write);

        // Act
        var response = await otherClinic.BookAsync(Request(Guid.NewGuid(), FutureSlot(hour: 14)), key, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tem espaço")]
    [InlineData("chave/com/barra")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task Book_WithMissingOrInvalidIdempotencyKey_ReturnsBadRequest(string? key)
    {
        // Act
        var response = await Writer().BookAsync(Request(_professional, FutureSlot()), key, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Idempotencia.ChaveInvalida", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Book_EndBeforeStart_ReturnsValidationProblem()
    {
        // Act
        var response = await Writer().BookAsync(Request(_professional, FutureSlot(), minutes: -30), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Agenda.IntervaloInvalido", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Book_MalformedJson_ReturnsBadRequest()
    {
        // Arrange
        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/appointments")
        {
            Content = new StringContent("{ isso não é json", System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("Idempotency-Key", NewKey());

        // Act
        var response = await Writer().SendAsync(message, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
