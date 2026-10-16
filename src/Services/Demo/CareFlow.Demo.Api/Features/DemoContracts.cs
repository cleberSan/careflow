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

/// <summary><c>access</c>, <c>portability</c> ou <c>erasure</c>.</summary>
public sealed record RightsRequestBody(string? Kind);

/// <summary>Código de 6 dígitos que chegou na caixa de entrada simulada.</summary>
public sealed record IdentityCodeBody(string? Code)
{
    public override string ToString() => "IdentityCodeBody { *** }";
}

public sealed record RightsServiceView(string Service, string? Outcome, DateTimeOffset? AnsweredAt);

public sealed record RightsReceiptLineView(string Service, string Item, string Description, string Action, int Records, DateTimeOffset? RetainedUntil, string Basis);

public sealed record RightsReceiptView(string PolicyVersion, DateTimeOffset ErasedAt, IReadOnlyList<RightsReceiptLineView> Lines);

public sealed record RightsPackageView(bool Available, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DestroyedAt);

/// <summary>Um pedido do titular no Painel: situação, prazo, andamento por serviço, comprovante e pacote.</summary>
public sealed record RightsRequestView(
    Guid Id,
    string Kind,
    string Status,
    string? DenialReason,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    bool Overdue,
    DateTimeOffset? CodeSentAt,
    string? CodeChannel,
    DateTimeOffset? IdentityConfirmedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<RightsServiceView> Services,
    RightsReceiptView? Receipt,
    RightsPackageView? Package,
    string? TraceId);

public sealed record InboxMessageView(string Channel, string Text, DateTimeOffset AcceptedAt);

public sealed record PackageFileView(string Name, long Bytes);

/// <summary>
/// O pacote como o titular o recebeu pelo link: os arquivos, as seções de cada serviço, os recursos FHIR e se o
/// mesmo link foi recusado na segunda vez.
/// </summary>
public sealed record PackageContentsView(
    IReadOnlyList<PackageFileView> Files,
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> FhirResources,
    string? PatientName,
    DateTimeOffset? LinkExpiresAt,
    bool ReuseRefused,
    string TraceId);

/// <summary>Depois da eliminação: o cofre não lê mais o paciente, e a cadeia da auditoria continua íntegra.</summary>
public sealed record ErasureEvidenceView(bool VaultReadable, int VaultStatus, IReadOnlyList<AuditEntryView> Audit, ChainView Chain);
