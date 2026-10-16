using System.Net;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.Consent.Api.Features.Consents;
using CareFlow.Consent.IntegrationTests.Infrastructure;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Rights;
using CareFlow.Testing;

using static CareFlow.Consent.IntegrationTests.Infrastructure.ConsentClient;

namespace CareFlow.Consent.IntegrationTests.Consents;

/// <summary>O Consent respondendo a pedidos do titular: histórico exportado e eliminação (ADR-0012).</summary>
public sealed class SubjectRequestTests(ConsentApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _patient = Guid.NewGuid();

    private HttpClient Client => api.CreateClient(_clinic, Read, Write);

    private SubjectRequestOpened Erasure() =>
        new(Guid.NewGuid(), _patient, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Export_CarriesCurrentConsentsAndEveryChange()
    {
        // Arrange: concede, revoga e concede de novo com outro termo.
        await Client.GrantAsync(_patient, ConsentPurposes.Scheduling, new GrantConsentRequest("consent", "2026-09"));
        await Client.RevokeAsync(_patient, ConsentPurposes.Scheduling);
        await Client.GrantAsync(_patient, ConsentPurposes.Scheduling, new GrantConsentRequest("consent", "2026-10"));
        await Client.GrantAsync(_patient, ConsentPurposes.AiTriage, Specific());
        using var key = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), _patient, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(key), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await api.DeliverAsync(_clinic, opened);

        // Assert
        var answer = (await api.TenantOutboxAsync(_clinic)).AnswerTo(opened.RequestId);
        var json = SealedBox.Open(key, answer.GetProperty("sealedSection").GetString()!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Consent));
        using var section = JsonDocument.Parse(json);
        Assert.Equal(2, section.RootElement.GetProperty("consents").GetArrayLength());
        var history = section.RootElement.GetProperty("history").EnumerateArray()
            .Where(h => h.GetProperty("purpose").GetString() == ConsentPurposes.Scheduling)
            .Select(h => (h.GetProperty("change").GetString(), h.GetProperty("termVersion").GetString(), h.GetProperty("version").GetInt64()))
            .ToList();
        Assert.Equal([("Granted", "2026-09", 1L), ("Revoked", "2026-09", 2L), ("Granted", "2026-10", 3L)], history);
    }

    [Fact]
    public async Task Erasure_RevokesWhatIsRevocable_KeepsHistory_AndRefusesNewGrants()
    {
        // Arrange
        await Client.GrantAsync(_patient, ConsentPurposes.Scheduling, new GrantConsentRequest("health-protection", "2026-10"));
        await Client.GrantAsync(_patient, ConsentPurposes.Notifications, new GrantConsentRequest("consent", "2026-10"));
        var opened = Erasure();

        // Act
        await api.DeliverAsync(_clinic, opened);
        var regrant = await Client.GrantAsync(_patient, ConsentPurposes.Notifications, new GrantConsentRequest("consent", "2026-10"));
        var newPurpose = await Client.GrantAsync(_patient, ConsentPurposes.AiTriage, Specific());

        // Assert
        var consents = await Client.ListOkAsync(_patient);
        Assert.Equal("revoked", consents.Single(c => c.Purpose == ConsentPurposes.Notifications).Status, ignoreCase: true);
        Assert.Equal("granted", consents.Single(c => c.Purpose == ConsentPurposes.Scheduling).Status, ignoreCase: true);

        var outbox = await api.TenantOutboxAsync(_clinic);
        var revoked = Assert.Single(outbox, row => row.Type == ConsentRevoked.EventType);
        Assert.Equal(ConsentPurposes.Notifications, revoked.Payload.GetProperty("purpose").GetString());

        var lines = outbox.AnswerTo(opened.RequestId).GetProperty("erasure").Deserialize<List<ErasureLine>>(Json)!;
        Assert.Equal([new ErasureLine(RetentionPolicy.Items.ConsentHistory, RetentionActions.Retained, 3)], lines);

        Assert.Equal(HttpStatusCode.Conflict, regrant.StatusCode);
        Assert.Equal("Consentimento.TitularEliminado", await regrant.ReadProblemCodeAsync());
        Assert.Equal("Consentimento.TitularEliminado", await newPurpose.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task ErasureReplay_IsIdempotent()
    {
        // Arrange
        await Client.GrantAsync(_patient, ConsentPurposes.Notifications, new GrantConsentRequest("consent", "2026-10"));
        var opened = Erasure();
        await api.DeliverAsync(_clinic, opened);

        // Act
        await api.DeliverAsync(_clinic, new ErasureReapplied(opened.RequestId, _patient, RetentionPolicy.Version, DateTimeOffset.UtcNow));

        // Assert: nenhuma revogação nova, nenhum erro.
        Assert.Single(await api.TenantOutboxAsync(_clinic), row => row.Type == ConsentRevoked.EventType);
        Assert.Equal(2L, await AdminSql.ScalarAsync<long>(api.AdminConnectionString, "SELECT count(*) FROM consent_history WHERE patient_ref = @p", ("p", _patient)));
    }

    [Fact]
    public async Task RequestFromAnotherClinic_NeverTouchesItsConsents()
    {
        // Arrange
        await Client.GrantAsync(_patient, ConsentPurposes.Notifications, new GrantConsentRequest("consent", "2026-10"));
        var otherClinic = Guid.NewGuid();

        // Act
        await api.DeliverAsync(otherClinic, Erasure());

        // Assert
        var consent = (await Client.ListOkAsync(_patient)).Single();
        Assert.Equal("granted", consent.Status, ignoreCase: true);
        var regrant = await Client.GrantAsync(_patient, ConsentPurposes.AiTriage, Specific());
        Assert.Equal(HttpStatusCode.Created, regrant.StatusCode);
    }
}
