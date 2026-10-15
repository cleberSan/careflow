using CareFlow.BuildingBlocks.Results;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Pseudonymization;

namespace CareFlow.Triage.Domain.Cases;

/// <summary>
/// Uma triagem: a queixa do paciente, o que foi enviado ao modelo (já pseudonimizado), a sugestão, as proteções
/// que agiram e a revisão humana. A sugestão nunca vale sozinha: <see cref="FinalPriority"/> só existe depois de
/// um profissional confirmar ou corrigir, e as duas coisas ficam registradas.
/// </summary>
public sealed class TriageCase
{
    public const int MinComplaintLength = 3;
    public const int MaxReviewerLength = 128;

    private TriageCase()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PatientRef { get; private set; }

    /// <summary>Texto como o paciente escreveu. Fica no banco do Triage, sob RLS; nunca sai do serviço.</summary>
    public string Complaint { get; private set; } = string.Empty;

    public string AgeBand { get; private set; } = string.Empty;

    /// <summary>Texto que o modelo recebeu, ou nulo se o modelo não foi chamado.</summary>
    public string? SentToModel { get; private set; }

    /// <summary>SHA-256 do pedido ao modelo (instruções, schema e caso); o mesmo valor vai para a auditoria.</summary>
    public string? PromptDigest { get; private set; }

    public string? PromptVersion { get; private set; }

    public string? ModelName { get; private set; }

    public ModelStatus ModelStatus { get; private set; }

    public TriagePriority? ModelPriority { get; private set; }

    public TriagePriority? SuggestedPriority { get; private set; }

    public SuggestionSource Source { get; private set; }

    public string? Rationale { get; private set; }

    /// <summary>Proteções que agiram, como <c>tipo:código</c>.</summary>
    public List<string> Guardrails { get; private set; } = [];

    public List<string> AlarmSigns { get; private set; } = [];

    /// <summary>Quantos identificadores de cada tipo foram mascarados (sem os valores).</summary>
    public Dictionary<string, int> Masks { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public ReviewDecision Decision { get; private set; }

    public TriagePriority? FinalPriority { get; private set; }

    /// <summary><c>sub</c> do profissional que revisou.</summary>
    public string? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public static Result<TriageCase> Open(Guid tenantId, Guid patientRef, string? complaint, TriageAssessment assessment, string ageBand, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (tenantId == Guid.Empty || patientRef == Guid.Empty)
        {
            return TriageErrors.MissingIdentifier;
        }

        if (!IsComplaint(complaint))
        {
            return TriageErrors.InvalidComplaint;
        }

        return new TriageCase
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            PatientRef = patientRef,
            Complaint = complaint!,
            AgeBand = ageBand,
            SentToModel = assessment.Prompt is null ? null : assessment.Masked?.Text,
            PromptDigest = assessment.Prompt?.Digest,
            PromptVersion = assessment.Prompt?.Version,
            ModelName = assessment.ModelName,
            ModelStatus = assessment.ModelStatus,
            ModelPriority = assessment.ModelPriority,
            SuggestedPriority = assessment.Priority,
            Source = assessment.Source,
            Rationale = assessment.Rationale,
            Guardrails = [.. assessment.Guardrails.Select(g => g.ToString())],
            AlarmSigns = [.. assessment.AlarmSigns.Select(a => a.Code)],
            Masks = assessment.Masked?.Counts.ToDictionary(c => c.Key.Slug(), c => c.Value) ?? [],
            CreatedAt = ToStoragePrecision(now),
            Decision = ReviewDecision.Pending,
        };
    }

    public static bool IsComplaint(string? value) =>
        value is not null && value.Trim().Length is >= MinComplaintLength and <= TriageEngine.MaxComplaintLength;

    /// <summary>
    /// Confirma a sugestão ou corrige a prioridade. Uma vez revisada, não muda: uma nova avaliação é uma nova
    /// triagem. Corrigir abaixo de um sinal de alarme é permitido (o profissional decide) e fica registrado.
    /// </summary>
    public Result Review(string? reviewer, ReviewDecision decision, TriagePriority? priority, DateTimeOffset now)
    {
        if (!IsReviewer(reviewer))
        {
            return Result.Failure(TriageErrors.InvalidReviewer);
        }

        if (Decision != ReviewDecision.Pending)
        {
            return Result.Failure(TriageErrors.AlreadyReviewed);
        }

        switch (decision)
        {
            case ReviewDecision.Confirmed when SuggestedPriority is null:
                return Result.Failure(TriageErrors.NothingToConfirm);
            case ReviewDecision.Confirmed:
                FinalPriority = SuggestedPriority;
                break;
            case ReviewDecision.Corrected when priority is null || !Enum.IsDefined(priority.Value):
                return Result.Failure(TriageErrors.InvalidPriority);
            case ReviewDecision.Corrected when priority == SuggestedPriority:
                return Result.Failure(TriageErrors.CorrectionMustDiffer);
            case ReviewDecision.Corrected:
                FinalPriority = priority;
                break;
            default:
                return Result.Failure(TriageErrors.InvalidDecision);
        }

        Decision = decision;
        ReviewedBy = reviewer;
        ReviewedAt = ToStoragePrecision(now);
        return Result.Success();
    }

    // Só ASCII visível, como os atores da auditoria.
    private static bool IsReviewer(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxReviewerLength && value.All(c => c is >= '!' and <= '~');

    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
