using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Pseudonymization;

namespace CareFlow.Triage.Api.Profiles;

/// <summary>Faixa etária e nome do paciente, lidos no cofre na hora da triagem e só mantidos em memória.</summary>
internal sealed record TriageProfile(AgeBand AgeBand, KnownIdentity Identity)
{
    public override string ToString() => $"TriageProfile {{ AgeBand = {AgeBand}, *** }}";
}

internal enum TriageProfileStatus
{
    Found,
    NotFound,
    Unavailable,
}

internal sealed record TriageProfileLookup(TriageProfileStatus Status, TriageProfile? Profile)
{
    public static readonly TriageProfileLookup NotFound = new(TriageProfileStatus.NotFound, null);
    public static readonly TriageProfileLookup Unavailable = new(TriageProfileStatus.Unavailable, null);

    public static TriageProfileLookup Found(TriageProfile profile) => new(TriageProfileStatus.Found, profile);
}

internal interface ITriageProfiles
{
    Task<TriageProfileLookup> FindAsync(Guid tenantId, Guid patientRef, CancellationToken cancellationToken);
}
