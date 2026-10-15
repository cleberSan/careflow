using CareFlow.Audit.Domain.Chain;

namespace CareFlow.Audit.UnitTests.Chain;

public sealed class ChainVerifierTests
{
    [Fact]
    public void Verify_EmptyChainWithGenesisHead_IsIntact()
    {
        // Act
        var result = ChainFixtures.Verify([], ChainHead.Genesis, ChainFixtures.NewKey());

        // Assert
        Assert.True(result.IsIntact);
        Assert.Equal(0, result.EntriesVerified);
        Assert.Equal(ChainFailure.None, result.Failure);
        Assert.Null(result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_ValidChain_IsIntact()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 5);

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), key);

        // Assert
        Assert.True(result.IsIntact);
        Assert.Equal(5, result.EntriesVerified);
        Assert.Equal(5, result.LastSequence);
    }

    [Fact]
    public void Verify_AlteredField_FailsAtThatEntry()
    {
        // Arrange: troca a ação da entrada 3 mantendo o hash gravado.
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 5);
        var original = chain[2];
        var forged = AuditEvent.Create(
            original.Event.TenantId, original.Event.MessageId, original.Event.OccurredAt, original.Event.SourceService,
            original.Event.Actor, "patient.updated", original.Event.ResourceType, original.Event.ResourceId).Value;
        chain[2] = original with { Event = forged };

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), key);

        // Assert
        Assert.False(result.IsIntact);
        Assert.Equal(ChainFailure.HashMismatch, result.Failure);
        Assert.Equal(3, result.BrokenAtSequence);
        Assert.Equal(2, result.EntriesVerified);
    }

    [Fact]
    public void Verify_AlteredRecordedAt_Fails()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 3);
        chain[0] = chain[0] with { RecordedAt = chain[0].RecordedAt.AddMinutes(-10) };

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), key);

        // Assert
        Assert.Equal(ChainFailure.HashMismatch, result.Failure);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_RemovedMiddleEntry_FailsWithSequenceGap()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 5);
        chain.RemoveAt(2);

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), key);

        // Assert
        Assert.Equal(ChainFailure.SequenceGap, result.Failure);
        Assert.Equal(3, result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_RenumberedAfterRemoval_FailsWithBrokenLink()
    {
        // Arrange: remove a entrada 2 e renumera a 3 para esconder o buraco.
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 4);
        chain.RemoveAt(1);
        chain[1] = chain[1] with { Sequence = 2 };

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), key);

        // Assert
        Assert.Equal(ChainFailure.BrokenLink, result.Failure);
        Assert.Equal(2, result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_RecomputedWithoutKey_FailsWithHashMismatch()
    {
        // Arrange: o atacante altera e recalcula a cadeia inteira, mas com outra chave.
        var realKey = ChainFixtures.NewKey();
        var attackerKey = ChainFixtures.NewKey();
        var forged = ChainFixtures.BuildChain(attackerKey, 3);

        // Act
        var result = ChainFixtures.Verify(forged, ChainFixtures.HeadOf(forged), realKey);

        // Assert
        Assert.Equal(ChainFailure.HashMismatch, result.Failure);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_TruncatedTail_FailsWithHeadMismatch()
    {
        // Arrange: apaga as duas últimas entradas; o que sobra ainda é uma cadeia válida.
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 5);
        var head = ChainFixtures.HeadOf(chain);
        chain.RemoveRange(3, 2);

        // Act
        var result = ChainFixtures.Verify(chain, head, key);

        // Assert
        Assert.Equal(ChainFailure.HeadMismatch, result.Failure);
        Assert.Equal(4, result.BrokenAtSequence);
        Assert.Equal(3, result.EntriesVerified);
    }

    [Fact]
    public void Verify_EntriesWithoutHead_FailsWithHeadMismatch()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 2);

        // Act
        var result = ChainFixtures.Verify(chain, ChainHead.Genesis, key);

        // Assert
        Assert.Equal(ChainFailure.HeadMismatch, result.Failure);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_UnknownKey_Fails()
    {
        // Arrange
        var chain = ChainFixtures.BuildChain(ChainFixtures.NewKey("antiga"), 2);

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), ChainFixtures.NewKey("nova"));

        // Assert
        Assert.Equal(ChainFailure.UnknownKey, result.Failure);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public void Verify_SupportsKeyRotation()
    {
        // Arrange: entradas 1-2 com a chave antiga, 3 com a nova.
        var oldKey = ChainFixtures.NewKey("k1");
        var newKey = ChainFixtures.NewKey("k2");
        var chain = ChainFixtures.BuildChain(oldKey, 2);
        var third = AuditChain.Append(ChainFixtures.HeadOf(chain), ChainFixtures.Event(), ChainFixtures.Now, newKey);
        chain.Add(third);

        // Act
        var result = ChainFixtures.Verify(chain, ChainFixtures.HeadOf(chain), oldKey, newKey);

        // Assert
        Assert.True(result.IsIntact);
        Assert.Equal(3, result.EntriesVerified);
    }

    [Fact]
    public void Accept_AfterFailure_IgnoresRemainingEntries()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 3);
        var verifier = new ChainVerifier(_ => key);

        // Act
        var first = verifier.Accept(chain[1]);
        var second = verifier.Accept(chain[0]);
        var result = verifier.Complete(ChainFixtures.HeadOf(chain));

        // Assert
        Assert.False(first);
        Assert.False(second);
        Assert.Equal(ChainFailure.SequenceGap, result.Failure);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public void Reject_MarksHashMismatchAtThatSequence()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var chain = ChainFixtures.BuildChain(key, 2);
        var verifier = new ChainVerifier(_ => key);
        verifier.Accept(chain[0]);

        // Act
        var rejected = verifier.Reject(2);
        var again = verifier.Reject(3);
        var result = verifier.Complete(ChainFixtures.HeadOf(chain));

        // Assert
        Assert.False(rejected);
        Assert.False(again);
        Assert.Equal(ChainFailure.HashMismatch, result.Failure);
        Assert.Equal(2, result.BrokenAtSequence);
        Assert.Equal(1, result.EntriesVerified);
    }
}
