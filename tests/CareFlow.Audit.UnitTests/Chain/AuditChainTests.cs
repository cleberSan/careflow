using System.Security.Cryptography;

using CareFlow.Audit.Domain.Chain;

namespace CareFlow.Audit.UnitTests.Chain;

public sealed class AuditChainTests
{
    [Fact]
    public void Append_FirstEntry_PointsToGenesis()
    {
        // Arrange
        var key = ChainFixtures.NewKey();

        // Act
        var entry = AuditChain.Append(ChainHead.Genesis, ChainFixtures.Event(), ChainFixtures.Now, key);

        // Assert
        Assert.Equal(1, entry.Sequence);
        Assert.Equal(new byte[AuditChain.HashSize], entry.PreviousHash);
        Assert.Equal(AuditChain.HashSize, entry.Hash.Length);
        Assert.Equal("k1", entry.KeyId);
    }

    [Fact]
    public void Append_LinksToPreviousHash()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var first = AuditChain.Append(ChainHead.Genesis, ChainFixtures.Event(), ChainFixtures.Now, key);

        // Act
        var second = AuditChain.Append(new ChainHead(first.Sequence, first.Hash), ChainFixtures.Event(), ChainFixtures.Now, key);

        // Assert
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.Hash, second.PreviousHash);
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var auditEvent = ChainFixtures.Event();
        var previous = RandomNumberGenerator.GetBytes(AuditChain.HashSize);

        // Act
        var a = AuditChain.ComputeHash(3, auditEvent, ChainFixtures.Now, previous, key);
        var b = AuditChain.ComputeHash(3, auditEvent, ChainFixtures.Now, previous, key);

        // Assert
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeHash_DependsOnKey()
    {
        // Arrange
        var auditEvent = ChainFixtures.Event();
        var previous = new byte[AuditChain.HashSize];

        // Act
        var withA = AuditChain.ComputeHash(1, auditEvent, ChainFixtures.Now, previous, ChainFixtures.NewKey());
        var withB = AuditChain.ComputeHash(1, auditEvent, ChainFixtures.Now, previous, ChainFixtures.NewKey());

        // Assert: sem a chave, quem altera o banco não consegue recalcular a cadeia.
        Assert.NotEqual(withA, withB);
    }

    [Fact]
    public void ComputeHash_TextFieldsAreLengthPrefixed()
    {
        // Arrange: mesmos caracteres, divididos em campos diferentes.
        var key = ChainFixtures.NewKey();
        var tenant = Guid.NewGuid();
        var message = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var a = AuditEvent.Create(tenant, message, ChainFixtures.Now, "ab", "c", "d", "e", resource).Value;
        var b = AuditEvent.Create(tenant, message, ChainFixtures.Now, "a", "bc", "d", "e", resource).Value;
        var previous = new byte[AuditChain.HashSize];

        // Act
        var hashA = AuditChain.ComputeHash(1, a, ChainFixtures.Now, previous, key);
        var hashB = AuditChain.ComputeHash(1, b, ChainFixtures.Now, previous, key);

        // Assert
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void ComputeHash_RejectsPreviousHashOfWrongSize()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            AuditChain.ComputeHash(1, ChainFixtures.Event(), ChainFixtures.Now, new byte[16], ChainFixtures.NewKey()));
    }

    [Fact]
    public void ChainKey_RejectsShortSecretAndHidesItInToString()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new ChainKey("k", new byte[ChainKey.MinimumKeySize - 1]));
        Assert.Throws<ArgumentException>(() => new ChainKey(" ", new byte[ChainKey.MinimumKeySize]));
        Assert.Equal("ChainKey(k9)", new ChainKey("k9", new byte[ChainKey.MinimumKeySize]).ToString());
    }
}
