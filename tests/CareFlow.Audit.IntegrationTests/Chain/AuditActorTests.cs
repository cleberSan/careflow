using System.Security.Claims;

using CareFlow.BuildingBlocks.AspNetCore.Security;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>Como quem fez a requisição aparece na trilha (calculado nos serviços que publicam o acesso).</summary>
public sealed class AuditActorTests
{
    private static ClaimsPrincipal User(string? subject) =>
        new(new ClaimsIdentity(subject is null ? [] : [new Claim("sub", subject)], "test"));

    [Fact]
    public void From_ValidSubject_ReturnsIt()
    {
        // Act & Assert
        Assert.Equal("usuario-123", AuditActor.From(User("usuario-123")));
    }

    [Fact]
    public void From_MissingSubjectOrUser_ReturnsUnknown()
    {
        // Act & Assert
        Assert.Equal(AuditActor.Unknown, AuditActor.From(User(null)));
        Assert.Equal(AuditActor.Unknown, AuditActor.From(null));
    }

    [Theory]
    [InlineData("usuário com espaço")]
    [InlineData("ação")]
    public void From_SubjectOutsideAuditFormat_ReturnsStableHash(string subject)
    {
        // Act
        var first = AuditActor.From(User(subject));
        var second = AuditActor.From(User(subject));

        // Assert
        Assert.StartsWith("sub-sha256:", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.DoesNotContain(subject, first, StringComparison.Ordinal);
    }

    [Fact]
    public void From_TooLongSubject_ReturnsHashWithinLimit()
    {
        // Act
        var actor = AuditActor.From(User(new string('a', AuditActor.MaxLength + 1)));

        // Assert
        Assert.StartsWith("sub-sha256:", actor, StringComparison.Ordinal);
        Assert.True(actor.Length <= AuditActor.MaxLength);
    }
}
