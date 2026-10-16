using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Pseudonymization;
using CareFlow.Triage.Domain.Safety;

namespace CareFlow.Triage.UnitTests.Triage;

public sealed class TriageCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static TriageAssessment Assessment(TriagePriority? priority, bool sent = true)
    {
        var masked = new MaskedComplaint("[nome] com tosse", new Dictionary<MaskKind, int> { [MaskKind.Name] = 1 }, 0.2);
        var prompt = sent ? TriagePrompt.Build(Domain.Patients.AgeBand.FromAge(30), masked.Text) : null;
        return new TriageAssessment(
            priority,
            priority is null ? SuggestionSource.None : SuggestionSource.Model,
            priority is null ? null : "Tosse há dois dias.",
            sent ? ModelStatus.Accepted : ModelStatus.SkippedNoConsent,
            priority,
            [new AlarmSign("dor-no-peito", TriagePriority.Emergency)],
            [new Guardrail(Guardrail.Alarm, "dor-no-peito")],
            sent ? masked : null,
            prompt,
            sent ? "fake" : null);
    }

    private static TriageCase Open(TriagePriority? priority = TriagePriority.LessUrgent, bool sent = true) =>
        TriageCase.Open(Guid.NewGuid(), Guid.NewGuid(), "Ana com tosse", Assessment(priority, sent), "18-39", Now.AddTicks(7)).Value;

    [Fact]
    public void Open_RecordsWhatWasSentAndTheSuggestion()
    {
        var triage = Open();

        Assert.NotEqual(Guid.Empty, triage.Id);
        Assert.Equal("Ana com tosse", triage.Complaint);
        Assert.Equal("[nome] com tosse", triage.SentToModel);
        Assert.Equal(64, triage.PromptDigest!.Length);
        Assert.Equal(TriagePrompt.Version, triage.PromptVersion);
        Assert.Equal("fake", triage.ModelName);
        Assert.Equal(TriagePriority.LessUrgent, triage.SuggestedPriority);
        Assert.Equal(["alarm:dor-no-peito"], triage.Guardrails);
        Assert.Equal(["dor-no-peito"], triage.AlarmSigns);
        Assert.Equal(1, triage.Masks["name"]);
        Assert.Equal(Now, triage.CreatedAt);
        Assert.Equal(ReviewDecision.Pending, triage.Decision);
        Assert.Null(triage.FinalPriority);
    }

    [Fact]
    public void Open_WhenNothingWasSent_HasNoPromptData()
    {
        var triage = Open(sent: false);

        Assert.Null(triage.SentToModel);
        Assert.Null(triage.PromptDigest);
        Assert.Empty(triage.Masks);
    }

    [Theory]
    [InlineData(true, false, "tosse", "Triagem.IdentificadorAusente")]
    [InlineData(false, true, "tosse", "Triagem.IdentificadorAusente")]
    [InlineData(false, false, "  a ", "Triagem.QueixaInvalida")]
    [InlineData(false, false, null, "Triagem.QueixaInvalida")]
    public void Open_InvalidInput_Fails(bool emptyTenant, bool emptyPatient, string? complaint, string code)
    {
        var result = TriageCase.Open(
            emptyTenant ? Guid.Empty : Guid.NewGuid(),
            emptyPatient ? Guid.Empty : Guid.NewGuid(),
            complaint,
            Assessment(TriagePriority.Urgent),
            "18-39",
            Now);

        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void Open_TooLongComplaint_Fails()
    {
        Assert.False(TriageCase.IsComplaint(new string('a', TriageEngine.MaxComplaintLength + 1)));
        Assert.True(TriageCase.IsComplaint(new string('a', TriageEngine.MaxComplaintLength)));
    }

    [Fact]
    public void Review_Confirm_KeepsTheSuggestion()
    {
        var triage = Open();

        var result = triage.Review("prof-123", ReviewDecision.Confirmed, null, Now.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(ReviewDecision.Confirmed, triage.Decision);
        Assert.Equal(TriagePriority.LessUrgent, triage.FinalPriority);
        Assert.Equal("prof-123", triage.ReviewedBy);
        Assert.Equal(Now.AddMinutes(5), triage.ReviewedAt);
    }

    [Fact]
    public void Review_Correct_RecordsBothSuggestionAndCorrection()
    {
        var triage = Open();

        var result = triage.Review("prof-123", ReviewDecision.Corrected, TriagePriority.Urgent, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(TriagePriority.LessUrgent, triage.SuggestedPriority);
        Assert.Equal(TriagePriority.Urgent, triage.FinalPriority);
        Assert.Equal(ReviewDecision.Corrected, triage.Decision);
    }

    [Fact]
    public void Review_WithoutSuggestion_RequiresCorrection()
    {
        var triage = Open(priority: null);

        Assert.Equal(TriageErrors.NothingToConfirm, triage.Review("prof", ReviewDecision.Confirmed, null, Now).Error);
        Assert.True(triage.Review("prof", ReviewDecision.Corrected, TriagePriority.NonUrgent, Now).IsSuccess);
    }

    [Theory]
    [InlineData(null, ReviewDecision.Confirmed, null, "Triagem.RevisorInvalido")]
    [InlineData("prof com espaço", ReviewDecision.Confirmed, null, "Triagem.RevisorInvalido")]
    [InlineData("prof", ReviewDecision.Pending, null, "Triagem.DecisaoInvalida")]
    [InlineData("prof", ReviewDecision.Corrected, null, "Triagem.PrioridadeInvalida")]
    [InlineData("prof", ReviewDecision.Corrected, (TriagePriority)42, "Triagem.PrioridadeInvalida")]
    [InlineData("prof", ReviewDecision.Corrected, TriagePriority.LessUrgent, "Triagem.CorrecaoIgualASugestao")]
    public void Review_InvalidInput_FailsAndChangesNothing(string? reviewer, ReviewDecision decision, TriagePriority? priority, string code)
    {
        var triage = Open();

        var result = triage.Review(reviewer, decision, priority, Now);

        Assert.Equal(code, result.Error.Code);
        Assert.Equal(ReviewDecision.Pending, triage.Decision);
        Assert.Null(triage.FinalPriority);
    }

    [Fact]
    public void Review_Twice_IsRejected()
    {
        var triage = Open();
        triage.Review("prof", ReviewDecision.Confirmed, null, Now);

        var second = triage.Review("prof", ReviewDecision.Corrected, TriagePriority.Emergency, Now);

        Assert.Equal(TriageErrors.AlreadyReviewed, second.Error);
        Assert.Equal(TriagePriority.LessUrgent, triage.FinalPriority);
    }

    [Fact]
    public void EraseText_RemovesTexts_KeepsSuggestionDigestAndDecision()
    {
        // Arrange
        var triage = Open();
        triage.Review("prof-1", ReviewDecision.Confirmed, null, Now);
        var digest = triage.PromptDigest;

        // Act
        var first = triage.EraseText(Now.AddDays(1));
        var second = triage.EraseText(Now.AddDays(2));

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.Null(triage.Complaint);
        Assert.Null(triage.SentToModel);
        Assert.Null(triage.Rationale);
        Assert.Equal(digest, triage.PromptDigest);
        Assert.Equal(TriagePriority.LessUrgent, triage.FinalPriority);
        Assert.Equal(["dor-no-peito"], triage.AlarmSigns);
        Assert.Equal(Now.AddDays(1), triage.ErasedAt);
    }
}

public sealed class TriageConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Projection_AppliesOnlyNewerVersions()
    {
        var consent = Domain.Consents.TriageConsent.Track(Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.False(consent.AllowsAi);

        Assert.True(consent.ApplyGranted(2, Now));
        Assert.True(consent.AllowsAi);
        Assert.False(consent.ApplyRevoked(1, Now));
        Assert.True(consent.AllowsAi);
        Assert.True(consent.ApplyRevoked(3, Now.AddMinutes(1)));
        Assert.False(consent.AllowsAi);
        Assert.False(consent.ApplyGranted(3, Now));
        Assert.Equal(3, consent.Version);
        Assert.Equal(Now.AddMinutes(1), consent.UpdatedAt);
    }

    [Fact]
    public void Track_RequiresIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => Domain.Consents.TriageConsent.Track(Guid.Empty, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => Domain.Consents.TriageConsent.Track(Guid.NewGuid(), Guid.Empty, Now));
    }
}
