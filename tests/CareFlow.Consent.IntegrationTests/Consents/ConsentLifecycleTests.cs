using System.Net;

using CareFlow.Consent.IntegrationTests.Infrastructure;

using static CareFlow.Consent.IntegrationTests.Infrastructure.ConsentClient;

namespace CareFlow.Consent.IntegrationTests.Consents;

public sealed class ConsentLifecycleTests(ConsentApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    private HttpClient Client() => api.CreateClient(_clinic, Read, Write);

    [Fact]
    public async Task Grant_FirstTime_CreatesAndPublishesEvents()
    {
        // Act
        var response = await Client().GrantAsync(_patient, "scheduling", Specific());

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/v1/patients/{_patient}/consents/scheduling", response.Headers.Location?.ToString());
        var consent = await response.ReadConsentAsync();
        Assert.Equal("scheduling", consent.Purpose);
        Assert.Equal("specific-consent", consent.LegalBasis);
        Assert.Equal("2026-10", consent.TermVersion);
        Assert.Equal("granted", consent.Status);
        Assert.Equal(1, consent.Version);

        var outbox = await OutboxAsync(api.AdminConnectionString, _patient);
        Assert.Equal(["consent.granted", "audit.data-access"], outbox.Select(e => e.Type));
        Assert.Equal(1, outbox[0].Payload.GetProperty("version").GetInt64());
        Assert.Equal("scheduling", outbox[0].Payload.GetProperty("purpose").GetString());
        Assert.Equal("consent.granted", outbox[1].Payload.GetProperty("action").GetString());
        Assert.Equal("usuario-de-teste", outbox[1].Payload.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Grant_SameAgain_IsIdempotentWithoutNewEvents()
    {
        // Arrange
        await Client().GrantAsync(_patient, "scheduling", Specific());

        // Act
        var response = await Client().GrantAsync(_patient, "scheduling", Specific());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.ReadConsentAsync()).Version);
        Assert.Equal(2, (await OutboxAsync(api.AdminConnectionString, _patient)).Count);
    }

    [Fact]
    public async Task Revoke_PublishesRevokedWithNextVersion()
    {
        // Arrange
        await Client().GrantAsync(_patient, "scheduling", Specific());

        // Act
        var response = await Client().RevokeAsync(_patient, "scheduling");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var consent = await response.ReadConsentAsync();
        Assert.Equal("revoked", consent.Status);
        Assert.NotNull(consent.RevokedAt);
        Assert.Equal(2, consent.Version);

        var revoked = (await OutboxAsync(api.AdminConnectionString, _patient)).Single(e => e.Type == "consent.revoked");
        Assert.Equal(2, revoked.Payload.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Revoke_Twice_IsIdempotent()
    {
        // Arrange
        await Client().GrantAsync(_patient, "scheduling", Specific());
        await Client().RevokeAsync(_patient, "scheduling");

        // Act
        var response = await Client().RevokeAsync(_patient, "scheduling");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.ReadConsentAsync()).Version);
        Assert.Single(await OutboxAsync(api.AdminConnectionString, _patient), e => e.Type == "consent.revoked");
    }

    [Fact]
    public async Task Grant_AfterRevocation_GrantsAgainWithVersionThree()
    {
        // Arrange
        await Client().GrantAsync(_patient, "scheduling", Specific());
        await Client().RevokeAsync(_patient, "scheduling");

        // Act
        var response = await Client().GrantAsync(_patient, "scheduling", Specific("2027-01"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var consent = await response.ReadConsentAsync();
        Assert.Equal("granted", consent.Status);
        Assert.Equal(3, consent.Version);
        Assert.Equal("2027-01", consent.TermVersion);
    }

    [Fact]
    public async Task Revoke_HealthProtection_IsRefused()
    {
        // Arrange
        await Client().GrantAsync(_patient, "scheduling", new("health-protection", "2026-10"));

        // Act
        var response = await Client().RevokeAsync(_patient, "scheduling");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Consentimento.BaseLegalNaoRevogavel", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Revoke_Unknown_ReturnsNotFound()
    {
        // Act
        var response = await Client().RevokeAsync(_patient, "notifications");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Consentimento.NaoEncontrado", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task List_ReturnsEachPurposeAndAuditsTheRead()
    {
        // Arrange
        await Client().GrantAsync(_patient, "scheduling", Specific());
        await Client().GrantAsync(_patient, "ai-triage", Specific());

        // Act
        var consents = await Client().ListOkAsync(_patient);

        // Assert
        Assert.Equal(["ai-triage", "scheduling"], consents.Select(c => c.Purpose));
        var reads = (await OutboxAsync(api.AdminConnectionString, _patient))
            .Where(e => e.Type == "audit.data-access" && e.Payload.GetProperty("action").GetString() == "consent.read");
        Assert.Single(reads);
    }

    [Theory]
    [InlineData("marketing", "specific-consent", "2026-10", "Consentimento.FinalidadeInvalida")]
    [InlineData("scheduling", "contrato", "2026-10", "Consentimento.BaseLegalInvalida")]
    [InlineData("scheduling", null, "2026-10", "Consentimento.BaseLegalInvalida")]
    [InlineData("scheduling", "consent", "versão 1", "Consentimento.VersaoDoTermoInvalida")]
    [InlineData("scheduling", "consent", null, "Consentimento.VersaoDoTermoInvalida")]
    public async Task Grant_WithInvalidInput_ReturnsBadRequest(string purpose, string? basis, string? term, string code)
    {
        // Act
        var response = await Client().GrantAsync(_patient, purpose, new(basis, term));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await response.ReadProblemCodeAsync());
        Assert.Empty(await OutboxAsync(api.AdminConnectionString, _patient));
    }

    [Fact]
    public async Task Revoke_InvalidPurpose_ReturnsBadRequest()
    {
        // Act
        var response = await Client().RevokeAsync(_patient, "marketing");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentFirstGrants_CreateOneRecord()
    {
        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Client().GrantAsync(_patient, "notifications", Specific())));

        // Assert: um 201; os outros veem o registro (200) ou perdem a corrida (409), nunca um segundo registro.
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var consents = await Client().ListOkAsync(_patient);
        Assert.Single(consents);
        Assert.Single(await OutboxAsync(api.AdminConnectionString, _patient), e => e.Type == "consent.granted");
    }
}
