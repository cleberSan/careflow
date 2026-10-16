using System.Globalization;
using System.Text.Json;

using CareFlow.Contracts.Rights;

using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CareFlow.Rights.Api.Features.Exports;

/// <summary>
/// Bundle FHIR R4 (tipo <c>collection</c>) com Patient, Appointment e Consent, para portabilidade (LGPD art. 18, V).
/// Só leitura: nada entra no CareFlow por FHIR. Validado contra os perfis base do R4 nos testes (Firely, ADR-0012).
/// As referências usam <c>urn:uuid</c> com os identificadores do CareFlow, então o Bundle é autocontido.
/// </summary>
internal static class FhirBundleBuilder
{
    /// <summary>Sistema de identificação do CPF na RNDS (Rede Nacional de Dados em Saúde).</summary>
    public const string CpfSystem = "http://rnds.saude.gov.br/fhir/r4/NamingSystem/cpf";

    public const string ProfessionalSystem = "urn:careflow:professional";
    public const string PurposeSystem = "urn:careflow:consent-purpose";
    public const string LegalBasisSystem = "urn:careflow:legal-basis";

    private static readonly JsonSerializerOptions FhirJson = new JsonSerializerOptions { WriteIndented = true }.ForFhir();

    public static Bundle Build(
        Guid requestId, VaultSection? vault, SchedulingSection? scheduling, ConsentSection? consent, Guid patientRef, DateTimeOffset generatedAt)
    {
        var bundle = new Bundle
        {
            Id = requestId.ToString(),
            Type = Bundle.BundleType.Collection,
            Timestamp = generatedAt,
        };

        var patientUrl = Urn(patientRef);
        if (vault is not null)
        {
            bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = patientUrl, Resource = Patient(vault) });
        }

        foreach (var appointment in scheduling?.Appointments ?? [])
        {
            bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = Urn(appointment.Id), Resource = Appointment(appointment, patientUrl) });
        }

        foreach (var current in consent?.Consents ?? [])
        {
            var id = DeterministicId(patientRef, current.Purpose);
            bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = Urn(id), Resource = Consent(current, id, patientUrl) });
        }

        return bundle;
    }

    public static byte[] Serialize(Bundle bundle) => JsonSerializer.SerializeToUtf8Bytes(bundle, FhirJson);

    private static Patient Patient(VaultSection vault)
    {
        var patient = new Patient
        {
            Id = vault.PatientRef.ToString(),
            Identifier = [new Identifier(CpfSystem, vault.Cpf)],
            Name = [new HumanName { Use = HumanName.NameUse.Official, Text = vault.FullName }],
            BirthDate = vault.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrEmpty(vault.Email))
        {
            patient.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Email, null, vault.Email));
        }

        if (!string.IsNullOrEmpty(vault.Phone))
        {
            patient.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Mobile, vault.Phone));
        }

        return patient;
    }

    private static Appointment Appointment(SchedulingSection.AppointmentItem item, string patientUrl)
    {
        var cancelled = string.Equals(item.Status, "Cancelled", StringComparison.Ordinal);
        var appointment = new Appointment
        {
            Id = item.Id.ToString(),
            Status = cancelled ? Hl7.Fhir.Model.Appointment.AppointmentStatus.Cancelled : Hl7.Fhir.Model.Appointment.AppointmentStatus.Booked,
            Start = item.StartsAt,
            End = item.EndsAt,
            Created = item.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
            Participant =
            [
                new Hl7.Fhir.Model.Appointment.ParticipantComponent
                {
                    Actor = new ResourceReference(patientUrl),
                    Status = ParticipationStatus.Accepted,
                },
                new Hl7.Fhir.Model.Appointment.ParticipantComponent
                {
                    Actor = new ResourceReference { Identifier = new Identifier(ProfessionalSystem, item.ProfessionalId.ToString()) },
                    Status = ParticipationStatus.Accepted,
                },
            ],
        };

        if (cancelled && item.CancellationReason is { } reason)
        {
            appointment.CancelationReason = new CodeableConcept { Text = reason };
        }

        return appointment;
    }

    private static Consent Consent(ConsentSection.Current current, Guid id, string patientUrl) => new()
    {
        Id = id.ToString(),
        Status = string.Equals(current.Status, "Granted", StringComparison.Ordinal)
            ? Hl7.Fhir.Model.Consent.ConsentState.Active
            : Hl7.Fhir.Model.Consent.ConsentState.Inactive,
        Scope = new CodeableConcept("http://terminology.hl7.org/CodeSystem/consentscope", "patient-privacy", "Privacy Consent"),
        Category = [new CodeableConcept("http://loinc.org", "59284-0", "Patient Consent")],
        Patient = new ResourceReference(patientUrl),
        DateTime = current.GrantedAt.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
        Policy = [new Hl7.Fhir.Model.Consent.PolicyComponent { Uri = $"urn:careflow:consent-term:{current.TermVersion}" }],
        PolicyRule = new CodeableConcept(LegalBasisSystem, current.LegalBasis),
        Provision = new Hl7.Fhir.Model.Consent.provisionComponent
        {
            Type = Hl7.Fhir.Model.Consent.ConsentProvisionType.Permit,
            Purpose = [new Coding(PurposeSystem, current.Purpose)],
        },
    };

    private static string Urn(Guid id) => $"urn:uuid:{id}";

    // O Consent não tem identificador próprio na seção; o par pseudônimo e finalidade é único na clínica.
    private static Guid DeterministicId(Guid patientRef, string purpose)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{patientRef:N}|{purpose}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
