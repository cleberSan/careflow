namespace CareFlow.Contracts.Rights;

/// <summary>
/// Seções do pacote do titular que o serviço de pedidos também lê para montar o Bundle FHIR. Viajam cifradas para o
/// pedido (<see cref="SubjectRequestAnswered.SealedSection"/>) e só existem decifradas no serviço que responde e no
/// serviço de pedidos, durante a montagem do pacote. As demais seções vão para o pacote como vieram.
/// </summary>
public sealed record VaultSection(
    Guid PatientRef,
    string FullName,
    string Cpf,
    DateOnly BirthDate,
    string? Email,
    string? Phone,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() => "VaultSection { *** }";
}

/// <summary>Seção da agenda: as consultas, sem nada além do pseudônimo.</summary>
public sealed record SchedulingSection(IReadOnlyList<SchedulingSection.AppointmentItem> Appointments)
{
    /// <summary><see cref="Status"/> é <c>Booked</c> ou <c>Cancelled</c>.</summary>
    public sealed record AppointmentItem(
        Guid Id,
        Guid ProfessionalId,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CancelledAt,
        string? CancellationReason);
}

/// <summary>Seção do Consent: a situação atual de cada finalidade e todo o histórico.</summary>
public sealed record ConsentSection(IReadOnlyList<ConsentSection.Current> Consents, IReadOnlyList<ConsentSection.HistoryItem> History)
{
    /// <summary><see cref="Status"/> é <c>Granted</c> ou <c>Revoked</c>.</summary>
    public sealed record Current(string Purpose, string LegalBasis, string TermVersion, string Status, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt, long Version);

    public sealed record HistoryItem(string Purpose, string LegalBasis, string TermVersion, string Change, long Version, DateTimeOffset OccurredAt);
}
