using System.Net;

using CareFlow.Scheduling.IntegrationTests.Infrastructure;

using static CareFlow.Scheduling.IntegrationTests.Infrastructure.SchedulingClient;

namespace CareFlow.Scheduling.IntegrationTests.Appointments;

public sealed class AuthorizationTests(SchedulingApiFixture api)
{
    [Fact]
    public async Task AnonymousRequest_ReturnsUnauthorized()
    {
        // Act
        var response = await api.CreateAnonymousClient()
            .BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyScope_CannotBook()
    {
        // Act
        var response = await api.CreateClient(Guid.NewGuid(), Read)
            .BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutClinic_IsForbidden()
    {
        // Act
        var response = await api.CreateClient(tenantId: null, Write)
            .BookAsync(Request(Guid.NewGuid(), FutureSlot()), NewKey(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnrelatedScope_CannotRead()
    {
        // Act
        var response = await api.CreateClient(Guid.NewGuid(), "profile")
            .GetAsync($"/v1/appointments/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreAnonymousAndHealthy(string path)
    {
        // Act
        var response = await api.CreateAnonymousClient().GetAsync(path, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
