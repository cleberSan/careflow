using CareFlow.Audit.Domain.Chain;

namespace CareFlow.Audit.UnitTests.Chain;

public sealed class AuditEventTests
{
    private static readonly DateTimeOffset Now = ChainFixtures.Now;

    [Fact]
    public void Create_WithValidData_KeepsValuesAndTruncatesToMicroseconds()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var occurredAt = Now.AddTicks(7);

        // Act
        var result = AuditEvent.Create(tenantId, messageId, occurredAt, "scheduling", "sub-123", "appointment.booked", "appointment", resourceId);

        // Assert
        Assert.True(result.IsSuccess);
        var auditEvent = result.Value;
        Assert.Equal(tenantId, auditEvent.TenantId);
        Assert.Equal(messageId, auditEvent.MessageId);
        Assert.Equal(Now, auditEvent.OccurredAt);
        Assert.Equal("scheduling", auditEvent.SourceService);
        Assert.Equal("sub-123", auditEvent.Actor);
        Assert.Equal("appointment.booked", auditEvent.Action);
        Assert.Equal("appointment", auditEvent.ResourceType);
        Assert.Equal(resourceId, auditEvent.ResourceId);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Create_WithEmptyIdentifier_Fails(bool emptyTenant, bool emptyMessage, bool emptyResource)
    {
        // Act
        var result = AuditEvent.Create(
            emptyTenant ? Guid.Empty : Guid.NewGuid(),
            emptyMessage ? Guid.Empty : Guid.NewGuid(),
            Now,
            "vault",
            "sub",
            "patient.read",
            "patient",
            emptyResource ? Guid.Empty : Guid.NewGuid());

        // Assert
        Assert.Equal(AuditErrors.MissingIdentifier, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Patient.Read")]
    [InlineData("patient read")]
    [InlineData("patient\nread")]
    [InlineData("ação")]
    public void Create_WithInvalidAction_Fails(string? action)
    {
        // Act
        var result = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "vault", "sub", action, "patient", Guid.NewGuid());

        // Assert
        Assert.Equal(AuditErrors.InvalidCode, result.Error);
    }

    [Fact]
    public void Create_AcceptsCodesAtMaximumLengthAndRejectsLonger()
    {
        // Arrange
        var atLimit = new string('a', AuditEvent.MaxCodeLength);
        var overLimit = new string('a', AuditEvent.MaxCodeLength + 1);

        // Act
        var accepted = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, atLimit, "sub", atLimit, atLimit, Guid.NewGuid());
        var rejected = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, overLimit, "sub", "a", "a", Guid.NewGuid());

        // Assert
        Assert.True(accepted.IsSuccess);
        Assert.Equal(AuditErrors.InvalidCode, rejected.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("linha\nforjada")]
    [InlineData("usuário")]
    public void Create_WithInvalidActor_Fails(string? actor)
    {
        // Act
        var result = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "vault", actor, "patient.read", "patient", Guid.NewGuid());

        // Assert
        Assert.Equal(AuditErrors.InvalidActor, result.Error);
    }

    [Fact]
    public void Create_AcceptsActorAtMaximumLengthAndRejectsLonger()
    {
        // Act
        var accepted = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "vault", new string('x', AuditEvent.MaxActorLength), "a", "a", Guid.NewGuid());
        var rejected = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "vault", new string('x', AuditEvent.MaxActorLength + 1), "a", "a", Guid.NewGuid());

        // Assert
        Assert.True(accepted.IsSuccess);
        Assert.Equal(AuditErrors.InvalidActor, rejected.Error);
    }

    [Fact]
    public void Create_WithContentDigest_KeepsIt()
    {
        // Arrange
        var digest = new string('a', 64);

        // Act
        var result = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "triage", "careflow-triage", "triage.suggested.model", "patient", Guid.NewGuid(), digest);

        // Assert
        Assert.Equal(digest, result.Value.ContentDigest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Create_WithMalformedContentDigest_Fails(string digest)
    {
        // Act
        var result = AuditEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "triage", "careflow-triage", "triage.suggested.model", "patient", Guid.NewGuid(), digest);

        // Assert
        Assert.Equal(AuditErrors.InvalidContentDigest, result.Error);
    }
}
