using System.Net;

using CareFlow.Consent.IntegrationTests.Infrastructure;

using static CareFlow.Consent.IntegrationTests.Infrastructure.ConsentClient;

namespace CareFlow.Consent.IntegrationTests.Consents;

public sealed class AuthorizationTests(ConsentApiFixture api)
{
    private readonly Guid _patient = Guid.NewGuid();

    [Fact]
    public async Task Anonymous_ReturnsUnauthorized()
    {
        // Act
        var client = api.CreateAnonymousClient();
        var list = await client.GetAsync($"/v1/patients/{_patient}/consents", Ct);
        var grant = await client.GrantAsync(_patient, "scheduling", Specific());
        var revoke = await client.RevokeAsync(_patient, "scheduling");

        // Assert
        Assert.All([list, grant, revoke], r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    [Fact]
    public async Task TokenWithoutClinic_IsForbidden()
    {
        // Act
        var response = await api.CreateClient(tenantId: null, Read, Write).GrantAsync(_patient, "scheduling", Specific());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadScope_CannotGrantOrRevoke()
    {
        // Arrange
        var reader = api.CreateClient(Guid.NewGuid(), Read);

        // Act
        var grant = await reader.GrantAsync(_patient, "scheduling", Specific());
        var revoke = await reader.RevokeAsync(_patient, "scheduling");
        var list = await reader.GetAsync($"/v1/patients/{_patient}/consents", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task Readiness_IsHealthy()
    {
        // Act
        var response = await api.CreateAnonymousClient().GetAsync("/health/ready", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
