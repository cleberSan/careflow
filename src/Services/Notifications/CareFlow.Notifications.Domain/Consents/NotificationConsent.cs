namespace CareFlow.Notifications.Domain.Consents;

/// <summary>
/// Projeção local do consentimento do paciente para a finalidade "notificações", alimentada pelos eventos do
/// Consent, como a agenda faz com "agendamento". Só versões maiores que a conhecida valem: duplicatas e
/// eventos atrasados são ignorados, então uma concessão antiga nunca desfaz uma revogação.
/// </summary>
public sealed class NotificationConsent
{
    private NotificationConsent()
    {
        // Usado pelo EF Core.
    }

    public Guid TenantId { get; private set; }

    public Guid PatientRef { get; private set; }

    public NotificationConsentState State { get; private set; }

    /// <summary>Versão do último evento aplicado (0 = nenhum).</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationConsent Track(Guid tenantId, Guid patientRef, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || patientRef == Guid.Empty)
        {
            throw new ArgumentException("Clínica e paciente são obrigatórios.");
        }

        return new NotificationConsent { TenantId = tenantId, PatientRef = patientRef, UpdatedAt = now };
    }

    public bool ApplyGranted(long version, DateTimeOffset now) => Apply(NotificationConsentState.Granted, version, now);

    public bool ApplyRevoked(long version, DateTimeOffset now) => Apply(NotificationConsentState.Revoked, version, now);

    private bool Apply(NotificationConsentState state, long version, DateTimeOffset now)
    {
        if (version <= Version)
        {
            return false;
        }

        State = state;
        Version = version;
        UpdatedAt = now;
        return true;
    }
}
