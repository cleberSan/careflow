using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.IntegrationTests.Infrastructure;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

public sealed class ChainLifecycleTests(AuditApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    [Fact]
    public async Task EmptyClinic_VerifiesAsIntact()
    {
        // Act
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.True(result.Intact);
        Assert.Equal(0, result.EntriesVerified);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task AppendedEvents_FormIntactChainInOrder()
    {
        // Arrange
        var patient = Guid.NewGuid();

        // Act
        await api.AppendAsync(NewEvent(_clinic, patient, "patient.registered"), Ct);
        await api.AppendAsync(NewEvent(_clinic, patient, "patient.read"), Ct);
        await api.AppendAsync(NewEvent(_clinic, patient, "patient.updated"), Ct);
        var client = api.CreateClient(_clinic, Read);
        var verification = await client.VerifyOkAsync();
        var page = await client.ListOkAsync();

        // Assert
        Assert.True(verification.Intact);
        Assert.Equal(3, verification.EntriesVerified);
        Assert.Equal(3, verification.LastSequence);
        Assert.Equal(["patient.registered", "patient.read", "patient.updated"], page.Items.Select(i => i.Action));
        Assert.Equal([1L, 2L, 3L], page.Items.Select(i => i.Sequence));
        Assert.All(page.Items, item => Assert.Equal(patient, item.ResourceId));
        Assert.Null(page.NextAfter);
    }

    [Fact]
    public async Task SameMessageTwice_IsRecordedOnce()
    {
        // Arrange
        var auditEvent = NewEvent(_clinic);

        // Act
        var first = await api.AppendAsync(auditEvent, Ct);
        var second = await api.AppendAsync(auditEvent, Ct);

        // Assert
        Assert.Equal(AppendOutcome.Appended, first);
        Assert.Equal(AppendOutcome.Duplicate, second);
        var page = await api.CreateClient(_clinic, Read).ListOkAsync();
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task ConcurrentAppends_ProduceContiguousIntactChain()
    {
        // Arrange
        const int count = 25;
        var events = Enumerable.Range(0, count).Select(_ => NewEvent(_clinic)).ToArray();

        // Act: gravações simultâneas da mesma clínica disputam a cabeça da cadeia.
        await Task.WhenAll(events.Select(e => api.AppendAsync(e, Ct)));

        // Assert
        var verification = await api.CreateClient(_clinic, Read).VerifyOkAsync();
        Assert.True(verification.Intact);
        Assert.Equal(count, verification.EntriesVerified);
        Assert.Equal(count, verification.LastSequence);
    }

    [Fact]
    public async Task ConcurrentDuplicates_AreRecordedOnce()
    {
        // Arrange
        var auditEvent = NewEvent(_clinic);

        // Act
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => api.AppendAsync(auditEvent, Ct)));

        // Assert
        Assert.Equal(1, outcomes.Count(o => o == AppendOutcome.Appended));
        Assert.True((await api.CreateClient(_clinic, Read).VerifyOkAsync()).Intact);
    }

    [Fact]
    public async Task List_FiltersByResourceAndPaginates()
    {
        // Arrange
        var patient = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await api.AppendAsync(NewEvent(_clinic, patient), Ct);
            await api.AppendAsync(NewEvent(_clinic), Ct);
        }

        var client = api.CreateClient(_clinic, Read);

        // Act
        var first = await client.ListOkAsync($"?resourceId={patient}&limit=3");
        var second = await client.ListOkAsync($"?resourceId={patient}&limit=3&after={first.NextAfter}");

        // Assert
        Assert.Equal(3, first.Items.Length);
        Assert.NotNull(first.NextAfter);
        Assert.Equal(2, second.Items.Length);
        Assert.Null(second.NextAfter);
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal(patient, item.ResourceId));
        Assert.Equal(5, first.Items.Concat(second.Items).Select(i => i.Sequence).Distinct().Count());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?after=-1")]
    public async Task List_WithInvalidPage_ReturnsBadRequest(string query)
    {
        // Act
        var response = await api.CreateClient(_clinic, Read).GetAsync($"/v1/audit/entries{query}", Ct);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Auditoria.PaginaInvalida", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Append_EventOfAnotherClinicThanScope_Throws()
    {
        // Arrange: escopo com uma clínica, evento de outra (erro de programação no consumidor).
        var auditEvent = NewEvent(Guid.NewGuid());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.AppendWithScopeAsync(_clinic, auditEvent, Ct));
    }
}
