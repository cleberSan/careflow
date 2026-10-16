using System.Security.Cryptography;

using CareFlow.Rights.Domain.Requests;

namespace CareFlow.Rights.UnitTests.Requests;

public sealed class SubjectRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RightHash = SHA256.HashData("123456"u8);
    private static readonly byte[] WrongHash = SHA256.HashData("654321"u8);

    private static SubjectRequest Received(SubjectRequestKind kind = SubjectRequestKind.Access) =>
        SubjectRequest.Receive(Guid.NewGuid(), Guid.NewGuid(), kind, _ => (byte[])RightHash.Clone(), Now).Value;

    [Fact]
    public void Receive_StartsTheDeadlineAndTheCode()
    {
        var request = Received();

        Assert.Equal(SubjectRequestStatus.Received, request.Status);
        Assert.Equal(Now.AddDays(15), request.DueAt);
        Assert.Equal(Now.AddMinutes(10), request.CodeExpiresAt);
        Assert.Equal(SubjectRequest.MaxCodeAttempts, request.RemainingAttempts);
        Assert.False(request.IsOverdue(Now.AddDays(15)));
        Assert.True(request.IsOverdue(Now.AddDays(15).AddTicks(10)));
    }

    [Fact]
    public void Receive_HashesTheCodeWithTheNewRequestId()
    {
        Guid seen = default;
        var request = SubjectRequest.Receive(Guid.NewGuid(), Guid.NewGuid(), SubjectRequestKind.Erasure, id => { seen = id; return RightHash; }, Now).Value;

        Assert.Equal(request.Id, seen);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Receive_WithoutClinicOrPatient_Fails(bool emptyTenant, bool emptyPatient)
    {
        var result = SubjectRequest.Receive(
            emptyTenant ? Guid.Empty : Guid.NewGuid(), emptyPatient ? Guid.Empty : Guid.NewGuid(), SubjectRequestKind.Access, _ => RightHash, Now);

        Assert.Equal(SubjectRequestErrors.MissingIdentifier, result.Error);
    }

    [Fact]
    public void Receive_UnknownKind_Fails()
    {
        var result = SubjectRequest.Receive(Guid.NewGuid(), Guid.NewGuid(), (SubjectRequestKind)99, _ => RightHash, Now);

        Assert.Equal(SubjectRequestErrors.InvalidKind, result.Error);
    }

    [Fact]
    public void Confirm_RightCode_MovesToInProgress_AndForgetsTheHash()
    {
        var request = Received();

        var result = request.ConfirmIdentity(RightHash, Now.AddMinutes(1));

        Assert.Equal(IdentityCheck.Confirmed, result.Value);
        Assert.Equal(SubjectRequestStatus.InProgress, request.Status);
        Assert.Null(request.CodeHash);
        Assert.Equal(Now.AddMinutes(1), request.IdentityConfirmedAt);
    }

    [Fact]
    public void Confirm_FifthWrongCode_Denies()
    {
        var request = Received();

        var results = Enumerable.Range(0, 5).Select(_ => request.ConfirmIdentity(WrongHash, Now).Value).ToList();

        Assert.Equal([IdentityCheck.WrongCode, IdentityCheck.WrongCode, IdentityCheck.WrongCode, IdentityCheck.WrongCode, IdentityCheck.AttemptsExhausted], results);
        Assert.Equal(SubjectRequestStatus.Denied, request.Status);
        Assert.Equal(DenialReason.IdentityNotConfirmed, request.DenialReason);
        Assert.Equal(SubjectRequestErrors.NotAwaitingIdentity, request.ConfirmIdentity(RightHash, Now).Error);
    }

    [Fact]
    public void Confirm_AtExpiry_Denies()
    {
        var request = Received();

        var result = request.ConfirmIdentity(RightHash, request.CodeExpiresAt);

        Assert.Equal(IdentityCheck.Expired, result.Value);
        Assert.Equal(DenialReason.IdentityNotConfirmed, request.DenialReason);
    }

    [Fact]
    public void Dispatch_Outcomes()
    {
        var sent = Received();
        sent.CodeDispatched(sent: true, noContact: false, "sms", Now);
        sent.CodeDispatched(sent: true, noContact: false, "email", Now.AddMinutes(1));
        var noContact = Received();
        noContact.CodeDispatched(sent: false, noContact: true, null, Now);
        var failed = Received();
        failed.CodeDispatched(sent: false, noContact: false, null, Now);

        Assert.Equal(("sms", Now), (sent.CodeChannel, sent.CodeSentAt!.Value));
        Assert.Equal(SubjectRequestStatus.Received, sent.Status);
        Assert.Equal(DenialReason.NoContact, noContact.DenialReason);
        Assert.Equal(DenialReason.CodeNotDelivered, failed.DenialReason);
    }

    [Fact]
    public void Dispatch_AfterConfirmation_ChangesNothing()
    {
        var request = Received();
        request.ConfirmIdentity(RightHash, Now);

        request.CodeDispatched(sent: false, noContact: true, null, Now);

        Assert.Equal(SubjectRequestStatus.InProgress, request.Status);
    }

    [Fact]
    public void Complete_DestroysTheRecipientKey()
    {
        var request = Received();
        request.ConfirmIdentity(RightHash, Now);
        var key = new byte[] { 1, 2, 3 };
        request.AttachRecipientKey(key);

        request.Complete(Now.AddHours(1));

        Assert.Equal(SubjectRequestStatus.Completed, request.Status);
        Assert.Null(request.WrappedRecipientKey);
        Assert.Equal(new byte[3], key);
        Assert.False(request.IsOverdue(Now.AddDays(30)));
    }

    [Fact]
    public void Deny_IsFinal_AndIdempotent()
    {
        var request = Received();
        request.ConfirmIdentity(RightHash, Now);
        request.AttachRecipientKey([9]);

        request.Deny(DenialReason.SubjectNotFound, Now);
        request.Deny(DenialReason.NoContact, Now.AddDays(1));

        Assert.Equal(DenialReason.SubjectNotFound, request.DenialReason);
        Assert.Equal(Now, request.CompletedAt);
        Assert.Null(request.WrappedRecipientKey);
        Assert.Throws<InvalidOperationException>(() => request.Complete(Now));
    }

    [Fact]
    public void AttachRecipientKey_OnlyForExportInProgress()
    {
        var erasure = Received(SubjectRequestKind.Erasure);
        erasure.ConfirmIdentity(RightHash, Now);
        var waiting = Received();

        Assert.Throws<InvalidOperationException>(() => erasure.AttachRecipientKey([1]));
        Assert.Throws<InvalidOperationException>(() => waiting.AttachRecipientKey([1]));
    }

    [Fact]
    public void DownloadLink_IsSingleUse_AndExpires()
    {
        var link = DownloadLink.Create(Guid.NewGuid(), Guid.NewGuid(), new byte[32], Now);
        var expired = DownloadLink.Create(Guid.NewGuid(), Guid.NewGuid(), new byte[32], Now);

        Assert.True(link.TryRedeem(Now.AddMinutes(14)));
        Assert.False(link.TryRedeem(Now.AddMinutes(14)));
        Assert.False(expired.TryRedeem(Now.AddMinutes(15)));
        Assert.Equal(Now.AddMinutes(15), expired.ExpiresAt);
    }

    [Fact]
    public void Package_KeepsSevenDays_AndDestroyWipesKeyAndContent()
    {
        var key = new byte[] { 7, 7, 7 };
        var package = SubjectRequestPackage.Create(Guid.NewGuid(), Guid.NewGuid(), [1, 2], key, 2, Now);

        Assert.True(package.IsAvailable(Now.AddDays(7).AddTicks(-10)));
        Assert.False(package.IsAvailable(Now.AddDays(7)));

        package.Destroy(Now.AddDays(7));
        package.Destroy(Now.AddDays(8));

        Assert.Null(package.Content);
        Assert.Null(package.WrappedKey);
        Assert.Equal(new byte[3], key);
        Assert.Equal(Now.AddDays(7), package.DestroyedAt);
        Assert.False(package.IsAvailable(Now));
    }
}
