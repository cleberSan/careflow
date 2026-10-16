using System.Net;

using CareFlow.Testing;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Triages;

/// <summary>Escopos por papel (menor privilégio), isolamento entre clínicas na API e no banco (RLS).</summary>
public sealed class AccessTests
{
    private readonly TriageApiFixture _api;
    private readonly Guid _clinic = Guid.NewGuid();

    public AccessTests(TriageApiFixture api)
    {
        _api = api;
        api.Reset();
    }

    [Fact]
    public async Task WithoutToken_IsUnauthorized()
    {
        // Act
        var response = await _api.CreateClient(null).SubmitAsync(Guid.NewGuid(), "Tosse há dois dias.");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scopes_FollowLeastPrivilege()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse há dois dias.");

        // Act
        var submitterReads = await _api.CreateClient(_clinic, Submit).GetAsync($"/v1/triages/{triage.Id}", Ct);
        var readerReviews = await _api.CreateClient(_clinic, Read).ReviewAsync(triage.Id, "confirm");
        var readerSubmits = await _api.CreateClient(_clinic, Read).SubmitAsync(patient, "Tosse.");
        var reviewerReads = await _api.CreateClient(_clinic, Review).GetAsync($"/v1/triages/{triage.Id}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, submitterReads.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readerReviews.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readerSubmits.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reviewerReads.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutClinic_IsForbidden()
    {
        // Act
        var response = await _api.CreateClient(null, Submit).SubmitAsync(Guid.NewGuid(), "Tosse há dois dias.");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OtherClinic_SeesNothing_ThroughTheApi()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse há dois dias.");
        var intruder = _api.CreateClient(Guid.NewGuid(), Read, Review);

        // Act
        var read = await intruder.GetAsync($"/v1/triages/{triage.Id}", Ct);
        var review = await intruder.ReviewAsync(triage.Id, "confirm");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);
    }

    [Fact]
    public async Task RowLevelSecurity_HidesOtherClinics_EvenWithDirectSql()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic);
        await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse há dois dias.");

        // Act
        await using var own = await TenantConnection.OpenAsync(_api.ApplicationConnectionString, _clinic);
        await using var other = await TenantConnection.OpenAsync(_api.ApplicationConnectionString, Guid.NewGuid());
        await using var none = await TenantConnection.OpenAsync(_api.ApplicationConnectionString, null);
        const string cases = "SELECT count(*) FROM triage_cases WHERE patient_ref = @p";
        const string consents = "SELECT count(*) FROM triage_consents WHERE patient_ref = @p";

        // Assert
        Assert.Equal(1, await own.CountAsync(cases, ("p", patient)));
        Assert.Equal(1, await own.CountAsync(consents, ("p", patient)));
        Assert.Equal(0, await other.CountAsync(cases, ("p", patient)));
        Assert.Equal(0, await other.CountAsync(consents, ("p", patient)));
        Assert.Equal(0, await none.CountAsync(cases, ("p", patient)));
    }

    [Fact]
    public async Task Database_RejectsInconsistentRows()
    {
        // Arrange: a regra "o que saiu tem digest" vale também no banco, contra bugs futuros.
        var patient = await _api.ConsentingPatientAsync(_clinic);
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Tosse há dois dias.");
        await using var own = await TenantConnection.OpenAsync(_api.ApplicationConnectionString, _clinic);

        // Act
        var dropDigest = () => own.ExecuteAsync("UPDATE triage_cases SET prompt_digest = NULL WHERE id = @id", ("id", triage.Id));
        var halfReview = () => own.ExecuteAsync("UPDATE triage_cases SET decision = 'Confirmed' WHERE id = @id", ("id", triage.Id));

        // Assert
        await Assert.ThrowsAsync<Npgsql.PostgresException>(dropDigest);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(halfReview);
    }
}
