using System.Net;

using CareFlow.Audit.IntegrationTests.Infrastructure;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

public sealed class AuthorizationTests(AuditApiFixture api)
{
    public static TheoryData<string> Endpoints => ["/v1/audit/entries", "/v1/audit/verification"];

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Anonymous_ReturnsUnauthorized(string path)
    {
        // Act
        var response = await api.CreateAnonymousClient().GetAsync(path, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task TokenWithoutClinic_IsForbidden(string path)
    {
        // Act
        var response = await api.CreateClient(tenantId: null, Read).GetAsync(path, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task OtherServiceScope_IsForbidden(string path)
    {
        // Act: escopo de leitura do cofre não dá acesso à trilha.
        var response = await api.CreateClient(Guid.NewGuid(), "vault.read").GetAsync(path, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Responses_AreNotCached()
    {
        // Act
        var response = await api.CreateClient(Guid.NewGuid(), Read).GetAsync("/v1/audit/entries", Ct);

        // Assert
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Readiness_IsHealthyWithNonPrivilegedLogin()
    {
        // Act
        var response = await api.CreateAnonymousClient().GetAsync("/health/ready", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
