namespace CareFlow.Demo.Api.Features;

public sealed record SessionView(DateTimeOffset ExpiresAt, string Partition, Guid PatientRef, int Bookings, int MaxBookings);

public sealed record BookingView(Guid AppointmentId, DateTimeOffset StartsAt, string TraceId, int Attempts, IReadOnlyList<string> Refusals);

public sealed record ActionView(string TraceId);

public sealed record VaultView(Guid PatientRef, string FullName, string Cpf, DateOnly BirthDate, string? Email, string? Phone);

public sealed record AppointmentView(Guid Id, Guid PatientRef, DateTimeOffset StartsAt, string Status, string? CancellationReason);

public sealed record ConsentView(string Purpose, string LegalBasis, string Status, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

public sealed record AttemptView(int Round, string Channel, string Outcome);

public sealed record NotificationView(Guid AppointmentId, string Kind, string Status, string? SentVia, string? NotSentReason, IReadOnlyList<AttemptView> Attempts);

/// <summary>O mesmo paciente em cada serviço: completo só no cofre, pseudônimo nos demais, mascarado nos logs.</summary>
public sealed record PatientViews(
    VaultView Vault,
    IReadOnlyList<AppointmentView> Scheduling,
    Guid ConsentPatientRef,
    IReadOnlyList<ConsentView> Consent,
    IReadOnlyList<NotificationView> Notifications,
    string LogMask);

/// <summary><see cref="ContentDigest"/>: SHA-256 do que saiu para o modelo, quando a entrada registra uma divulgação.</summary>
public sealed record AuditEntryView(long Sequence, DateTimeOffset OccurredAt, string SourceService, string Action, string ResourceType, string? ContentDigest = null);

public sealed record ChainView(bool Intact, long EntriesVerified, long LastSequence);

public sealed record PropagationView(
    IReadOnlyList<AppointmentView> Appointments,
    IReadOnlyList<NotificationView> Notifications,
    IReadOnlyList<AuditEntryView> Audit,
    ChainView Chain);

public sealed record ChannelView(string Channel, string? Fault, DateTimeOffset? FaultExpiresAt, string Circuit);

public sealed record ChannelsView(string Partition, IReadOnlyList<ChannelView> Channels);

/// <summary>Queixa em texto livre, como o paciente escreveria. Só dados sintéticos.</summary>
public sealed record TriageRequestBody(string? Complaint)
{
    public override string ToString() => "TriageRequestBody { *** }";
}

/// <summary><c>confirm</c> ou <c>correct</c> (com a prioridade escolhida pelo profissional).</summary>
public sealed record TriageReviewBody(string? Decision, string? Priority);

/// <summary>
/// Uma triagem no Painel: o que o paciente escreveu, o que saiu para o modelo (e o resumo que está na trilha), a
/// sugestão, as proteções acionadas e a revisão humana, com o trace da ação.
/// </summary>
public sealed record TriageView(
    Guid Id,
    string Complaint,
    string AgeBand,
    string? SentToModel,
    string? PromptDigest,
    string? PromptVersion,
    string? ModelName,
    string ModelStatus,
    string? ModelPriority,
    string? SuggestedPriority,
    string Source,
    string? Rationale,
    IReadOnlyList<string> Guardrails,
    IReadOnlyList<string> AlarmSigns,
    IReadOnlyDictionary<string, int> Masks,
    string Decision,
    string? FinalPriority,
    DateTimeOffset? ReviewedAt,
    string TraceId)
{
    public override string ToString() => $"TriageView {{ Id = {Id}, *** }}";
}

public sealed record AiConsentView(string State, long Version);
