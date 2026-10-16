using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Auditing;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>Eventos <c>audit.content-disclosed</c>: a trilha guarda o resumo do que saiu, coberto pela cadeia.</summary>
public sealed class ContentDisclosureTests(AuditApiFixture api)
{
    private const string Digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _resource = Guid.NewGuid();

    private ContentDisclosed Disclosed(string digest = Digest) =>
        new("triage", "careflow-triage", "triage.suggested.model", "patient", _resource, digest, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Deliver_AppendsWithTheDigest_AndTheChainStaysIntact()
    {
        // Arrange
        await api.DeliverAsync(_clinic, new DataAccessRecorded("triage", "sub-1", "triage.read", "patient", _resource, DateTimeOffset.UtcNow), Guid.CreateVersion7());

        // Act
        var outcome = await api.DeliverAsync(_clinic, Disclosed(), Guid.CreateVersion7());

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var client = api.CreateClient(_clinic, Read);
        var entries = (await client.ListOkAsync($"?resourceId={_resource}")).Items;
        Assert.Equal([null, Digest], entries.Select(e => e.ContentDigest));
        Assert.Equal("triage.suggested.model", entries[1].Action);
        Assert.True((await client.VerifyOkAsync()).Intact);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("9f86d081")]
    [InlineData("queixa: dor no peito há 2 dias, sem febre, nada mais a declarar....")]
    public async Task Deliver_MalformedDigest_IsRejectedAsPoison(string digest)
    {
        // Act
        var outcome = await api.DeliverAsync(_clinic, Disclosed(digest), Guid.CreateVersion7());

        // Assert: um resumo inválido poderia ser o próprio conteúdo; não entra na cadeia.
        Assert.Equal(ProcessOutcome.Invalid, outcome);
        Assert.Empty((await api.CreateClient(_clinic, Read).ListOkAsync($"?resourceId={_resource}")).Items);
    }
}
