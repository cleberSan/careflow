using System.Collections.Concurrent;

using CareFlow.Triage.Api.Profiles;
using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Pseudonymization;

namespace CareFlow.Triage.IntegrationTests.Infrastructure;

/// <summary>Cofre em memória: idade e nome sintéticos por paciente. Paciente não cadastrado responde "não encontrado".</summary>
internal sealed class FakeProfiles : ITriageProfiles
{
    private readonly ConcurrentDictionary<Guid, TriageProfileLookup> _patients = new();

    public Guid Add(int age, string fullName)
    {
        var patientRef = Guid.NewGuid();
        _patients[patientRef] = TriageProfileLookup.Found(new TriageProfile(AgeBand.FromAge(age), KnownIdentity.FromFullName(fullName)));
        return patientRef;
    }

    public Guid AddUnavailable()
    {
        var patientRef = Guid.NewGuid();
        _patients[patientRef] = TriageProfileLookup.Unavailable;
        return patientRef;
    }

    public Task<TriageProfileLookup> FindAsync(Guid tenantId, Guid patientRef, CancellationToken cancellationToken) =>
        Task.FromResult(_patients.TryGetValue(patientRef, out var lookup) ? lookup : TriageProfileLookup.NotFound);
}
