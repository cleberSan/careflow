using CareFlow.Audit.IntegrationTests.Infrastructure;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>Isolamento entre clínicas em cada endpoint da auditoria.</summary>
public sealed class ClinicIsolationTests(AuditApiFixture api)
{
    private readonly Guid _clinicA = Guid.NewGuid();
    private readonly Guid _clinicB = Guid.NewGuid();

    [Fact]
    public async Task List_NeverReturnsEntriesOfAnotherClinic()
    {
        // Arrange
        var patient = Guid.NewGuid();
        await api.AppendAsync(NewEvent(_clinicA, patient), Ct);

        // Act: a clínica B pede a trilha do pseudônimo da clínica A.
        var all = await api.CreateClient(_clinicB, Read).ListOkAsync();
        var byResource = await api.CreateClient(_clinicB, Read).ListOkAsync($"?resourceId={patient}");

        // Assert
        Assert.Empty(all.Items);
        Assert.Empty(byResource.Items);
    }

    [Fact]
    public async Task Verification_CoversOnlyOwnClinic()
    {
        // Arrange
        await api.AppendAsync(NewEvent(_clinicA), Ct);
        await api.AppendAsync(NewEvent(_clinicA), Ct);
        await api.AppendAsync(NewEvent(_clinicB), Ct);

        // Act
        var a = await api.CreateClient(_clinicA, Read).VerifyOkAsync();
        var b = await api.CreateClient(_clinicB, Read).VerifyOkAsync();

        // Assert: cada clínica tem a sua própria cadeia, começando em 1.
        Assert.Equal(2, a.LastSequence);
        Assert.Equal(1, b.LastSequence);
        Assert.True(a.Intact && b.Intact);
    }
}
