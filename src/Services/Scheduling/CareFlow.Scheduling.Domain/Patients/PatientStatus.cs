using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Scheduling.Domain.Patients;

/// <summary>
/// Projeção local, alimentada por eventos, do que a agenda precisa saber de um paciente: se ele existe no
/// cofre e se há base legal para agendamento. Não guarda nenhum dado pessoal. Eventos de consentimento só
/// valem se trouxerem versão maior que a conhecida: duplicatas e eventos atrasados são ignorados, então
/// uma concessão antiga que chegue depois nunca desfaz uma revogação.
/// </summary>
public sealed class PatientStatus
{
    private PatientStatus()
    {
        // Usado pelo EF Core.
    }

    public Guid TenantId { get; private set; }

    public Guid PatientRef { get; private set; }

    public bool IsRegistered { get; private set; }

    public SchedulingConsent SchedulingConsent { get; private set; }

    /// <summary>Versão do último evento de consentimento aplicado (0 = nenhum).</summary>
    public long ConsentVersion { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PatientStatus Track(Guid tenantId, Guid patientRef, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || patientRef == Guid.Empty)
        {
            throw new ArgumentException("Clínica e paciente são obrigatórios.");
        }

        return new PatientStatus { TenantId = tenantId, PatientRef = patientRef, UpdatedAt = now };
    }

    public void MarkRegistered(DateTimeOffset now)
    {
        if (!IsRegistered)
        {
            IsRegistered = true;
            UpdatedAt = now;
        }
    }

    /// <summary>Aplica uma concessão. Devolve <c>false</c> se a versão não for nova (ignorada).</summary>
    public bool ApplyConsentGranted(long version, DateTimeOffset now) => Apply(SchedulingConsent.Granted, version, now);

    /// <summary>Aplica uma revogação. Devolve <c>false</c> se a versão não for nova (ignorada).</summary>
    public bool ApplyConsentRevoked(long version, DateTimeOffset now) => Apply(SchedulingConsent.Revoked, version, now);

    /// <summary>Pode reservar? Só paciente cadastrado e com base legal ativa para agendamento.</summary>
    public Result EnsureCanBook()
    {
        if (!IsRegistered)
        {
            return Result.Failure(PatientErrors.NotYetKnown);
        }

        return SchedulingConsent == SchedulingConsent.Granted
            ? Result.Success()
            : Result.Failure(PatientErrors.NoLegalBasis);
    }

    private bool Apply(SchedulingConsent consent, long version, DateTimeOffset now)
    {
        if (version <= ConsentVersion)
        {
            return false;
        }

        SchedulingConsent = consent;
        ConsentVersion = version;
        UpdatedAt = now;
        return true;
    }
}
