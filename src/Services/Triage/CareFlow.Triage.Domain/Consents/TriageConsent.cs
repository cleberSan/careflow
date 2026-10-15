namespace CareFlow.Triage.Domain.Consents;

/// <summary>
/// Projeção local do consentimento do paciente para a finalidade "ai-triage", alimentada pelos eventos do
/// Consent, como a agenda e o Notifications fazem com as finalidades deles. Só versões maiores que a conhecida
/// valem: duplicatas e eventos atrasados são ignorados, então uma concessão antiga nunca desfaz uma revogação.
/// Sem linha, ou em <see cref="TriageConsentState.Unknown"/>, não há chamada à IA.
/// </summary>
public sealed class TriageConsent
{
    private TriageConsent()
    {
        // Usado pelo EF Core.
    }

    public Guid TenantId { get; private set; }

    public Guid PatientRef { get; private set; }

    public TriageConsentState State { get; private set; }

    /// <summary>Versão do último evento aplicado (0 = nenhum).</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool AllowsAi => State == TriageConsentState.Granted;

    public static TriageConsent Track(Guid tenantId, Guid patientRef, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || patientRef == Guid.Empty)
        {
            throw new ArgumentException("Clínica e paciente são obrigatórios.");
        }

        return new TriageConsent { TenantId = tenantId, PatientRef = patientRef, UpdatedAt = now };
    }

    public bool ApplyGranted(long version, DateTimeOffset now) => Apply(TriageConsentState.Granted, version, now);

    public bool ApplyRevoked(long version, DateTimeOffset now) => Apply(TriageConsentState.Revoked, version, now);

    private bool Apply(TriageConsentState state, long version, DateTimeOffset now)
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

public enum TriageConsentState
{
    /// <summary>Nenhum evento de consentimento ai-triage chegou ainda.</summary>
    Unknown = 0,
    Granted,
    Revoked,
}
