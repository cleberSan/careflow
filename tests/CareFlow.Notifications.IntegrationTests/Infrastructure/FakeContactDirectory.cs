using System.Collections.Concurrent;

using CareFlow.Notifications.Api.Contacts;

namespace CareFlow.Notifications.IntegrationTests.Infrastructure;

/// <summary>Cofre de mentira: contatos sintéticos por clínica e paciente; desconhecido é "não encontrado".</summary>
internal sealed class FakeContactDirectory : IContactDirectory
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Patient), ContactLookup> _lookups = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Patient), int> _reads = new();

    public void Set(Guid tenantId, Guid patientRef, ContactLookup lookup) => _lookups[(tenantId, patientRef)] = lookup;

    public int ReadsOf(Guid tenantId, Guid patientRef) => _reads.GetValueOrDefault((tenantId, patientRef));

    public Task<ContactLookup> FindAsync(Guid tenantId, Guid patientRef, CancellationToken cancellationToken)
    {
        _reads.AddOrUpdate((tenantId, patientRef), 1, (_, count) => count + 1);
        return Task.FromResult(_lookups.GetValueOrDefault((tenantId, patientRef), ContactLookup.NotFound));
    }
}
