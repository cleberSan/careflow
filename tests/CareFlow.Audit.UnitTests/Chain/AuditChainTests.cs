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

    [Fact]
    public void ComputeHash_WithoutDigest_MatchesTheOriginalEncoding()
    {
        // Arrange: vetor fixo. Se mudar, as cadeias já gravadas deixam de verificar.
        var key = new ChainKey("k1", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var auditEvent = AuditEvent.Create(
            new Guid("0192a000-0000-7000-8000-000000000001"),
            new Guid("0192a000-0000-7000-8000-000000000002"),
            ChainFixtures.Now,
            "patient-vault",
            "usuario-1",
            "patient.read",
            "patient",
            new Guid("0192a000-0000-7000-8000-000000000003")).Value;

        // Act
        var hash = AuditChain.ComputeHash(1, auditEvent, ChainFixtures.Now, new byte[AuditChain.HashSize], key);

        // Assert
        Assert.Equal(GoldenHash, Convert.ToHexStringLower(hash));
    }

    [Fact]
    public void ComputeHash_CoversTheContentDigest()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var previous = new byte[AuditChain.HashSize];
        var tenant = Guid.NewGuid();
        var message = Guid.NewGuid();
        var resource = Guid.NewGuid();
        AuditEvent With(string? digest) =>
            AuditEvent.Create(tenant, message, ChainFixtures.Now, "triage", "careflow-triage", "triage.suggested.model", "patient", resource, digest).Value;

        // Act
        var none = AuditChain.ComputeHash(1, With(null), ChainFixtures.Now, previous, key);
        var a = AuditChain.ComputeHash(1, With(new string('a', 64)), ChainFixtures.Now, previous, key);
        var b = AuditChain.ComputeHash(1, With(new string('b', 64)), ChainFixtures.Now, previous, key);

        // Assert
        Assert.NotEqual(none, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verify_SwappedContentDigest_BreaksTheChain()
    {
        // Arrange
        var key = ChainFixtures.NewKey();
        var original = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), ChainFixtures.Now, "triage", "careflow-triage", "triage.suggested.model", "patient", Guid.NewGuid(), new string('a', 64)).Value;
        var entry = AuditChain.Append(ChainHead.Genesis, original, ChainFixtures.Now, key);
        var forged = AuditEvent.Create(original.TenantId, original.MessageId, original.OccurredAt, original.SourceService, original.Actor, original.Action, original.ResourceType, original.ResourceId, new string('b', 64)).Value;

        // Act
        var result = ChainFixtures.Verify([entry with { Event = forged }], new ChainHead(entry.Sequence, entry.Hash), key);

        // Assert
        Assert.False(result.IsIntact);
    }

    private const string GoldenHash = "530889753108d6f1a6be77a26ebd29f42f70510bf76e9ed77c2e51f686c95dc1";
}
