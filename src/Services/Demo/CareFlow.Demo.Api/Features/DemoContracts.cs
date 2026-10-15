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

public sealed record AuditEntryView(long Sequence, DateTimeOffset OccurredAt, string SourceService, string Action, string ResourceType);

public sealed record ChainView(bool Intact, long EntriesVerified, long LastSequence);

public sealed record PropagationView(
    IReadOnlyList<AppointmentView> Appointments,
    IReadOnlyList<NotificationView> Notifications,
    IReadOnlyList<AuditEntryView> Audit,
    ChainView Chain);

public sealed record ChannelView(string Channel, string? Fault, DateTimeOffset? FaultExpiresAt, string Circuit);

public sealed record ChannelsView(string Partition, IReadOnlyList<ChannelView> Channels);
