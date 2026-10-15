using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Auditing;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>Eventos <c>audit.data-access</c> dos outros serviços entrando na cadeia pelo consumidor com Inbox.</summary>
public sealed class AccessEventConsumptionTests(AuditApiFixture api)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _resource = Guid.NewGuid();

    private DataAccessRecorded Access(string action = "patient.read") =>
        new("patient-vault", "usuario-sintetico", action, "patient", _resource, Now);

    [Fact]
    public async Task Deliver_AppendsToTheClinicFromTheEnvelope()
    {
        // Act
        var outcome = await api.DeliverAsync(_clinic, Access(), Guid.CreateVersion7());

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var entry = Assert.Single((await api.CreateClient(_clinic, Read).ListOkAsync($"?resourceId={_resource}")).Items);
        Assert.Equal("patient-vault", entry.SourceService);
        Assert.Equal("usuario-sintetico", entry.Actor);
        Assert.Equal("patient.read", entry.Action);
        Assert.Empty((await api.CreateClient(Guid.NewGuid(), Read).ListOkAsync($"?resourceId={_resource}")).Items);
        Assert.True((await api.CreateClient(_clinic, Read).VerifyOkAsync()).Intact);
    }

    [Fact]
    public async Task Deliver_SameMessageTwice_AppendsOnce()
    {
        // Arrange
        var messageId = Guid.CreateVersion7();

        // Act
        var first = await api.DeliverAsync(_clinic, Access(), messageId);
        var second = await api.DeliverAsync(_clinic, Access(), messageId);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, first);
        Assert.Equal(ProcessOutcome.Duplicate, second);
        Assert.Single((await api.CreateClient(_clinic, Read).ListOkAsync($"?resourceId={_resource}")).Items);
    }

    [Theory]
    [InlineData("Patient Read")]
    [InlineData("")]
    public async Task Deliver_InvalidAction_IsRejectedAsPoison(string action)
    {
        // Act
        var outcome = await api.DeliverAsync(_clinic, Access(action), Guid.CreateVersion7());

        // Assert: nunca passaria na validação; vai para mensagens mortas e não entra na cadeia.
        Assert.Equal(ProcessOutcome.Invalid, outcome);
        Assert.Empty((await api.CreateClient(_clinic, Read).ListOkAsync($"?resourceId={_resource}")).Items);
    }
}
