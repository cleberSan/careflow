using System.Security.Cryptography;

using CareFlow.Audit.Domain.Chain;

namespace CareFlow.Audit.UnitTests.Chain;

internal static class ChainFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public static ChainKey NewKey(string id = "k1") => new(id, RandomNumberGenerator.GetBytes(32));

    public static AuditEvent Event(Guid? tenantId = null, string action = "patient.read", Guid? resourceId = null) =>
        AuditEvent.Create(
            tenantId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            Now,
            "patient-vault",
            "usuario-1",
            action,
            "patient",
            resourceId ?? Guid.NewGuid()).Value;

    /// <summary>Monta uma cadeia válida de <paramref name="count"/> entradas da mesma clínica.</summary>
    public static List<ChainedEntry> BuildChain(ChainKey key, int count)
    {
        var tenantId = Guid.NewGuid();
        var head = ChainHead.Genesis;
        var entries = new List<ChainedEntry>();
        for (var i = 0; i < count; i++)
        {
            var entry = AuditChain.Append(head, Event(tenantId), Now.AddSeconds(i), key);
            entries.Add(entry);
            head = new ChainHead(entry.Sequence, entry.Hash);
        }

        return entries;
    }

    public static ChainHead HeadOf(List<ChainedEntry> entries) =>
        entries.Count == 0 ? ChainHead.Genesis : new ChainHead(entries[^1].Sequence, entries[^1].Hash);

    public static ChainVerification Verify(IEnumerable<ChainedEntry> entries, ChainHead head, params ChainKey[] keys)
    {
        var verifier = new ChainVerifier(id => keys.FirstOrDefault(k => k.Id == id));
        foreach (var entry in entries)
        {
            if (!verifier.Accept(entry))
            {
                break;
            }
        }

        return verifier.Complete(head);
    }
}
