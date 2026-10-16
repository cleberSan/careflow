using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Consent.Api.Infrastructure.Persistence;
using CareFlow.Consent.Domain.Consents;
using CareFlow.Contracts.Consents;

namespace CareFlow.Consent.Api.Features.Consents;

/// <summary>
/// Eventos que o Consent coloca no Outbox e a linha do histórico, sempre na mesma transação da mudança.
/// </summary>
internal static class ConsentEvents
{
    public const string SourceService = "consent";
    public const string ResourceType = "patient";

    public static void Granted(ConsentDbContext db, ConsentRecord consent, string actor, DateTimeOffset now)
    {
        db.History.Add(ConsentHistoryEntry.Of(consent, ConsentChange.Granted, now));
        db.Enqueue(
            consent.TenantId,
            new ConsentGranted(
                consent.PatientRef,
                ConsentNames.Of(consent.Purpose),
                ConsentNames.Of(consent.LegalBasis),
                consent.TermVersion,
                consent.Version,
                consent.GrantedAt),
            now);
        db.RecordAccess(consent.TenantId, SourceService, actor, "consent.granted", ResourceType, consent.PatientRef, now);
    }

    public static void Revoked(ConsentDbContext db, ConsentRecord consent, string actor, DateTimeOffset now)
    {
        db.History.Add(ConsentHistoryEntry.Of(consent, ConsentChange.Revoked, now));
        db.Enqueue(
            consent.TenantId,
            new ConsentRevoked(consent.PatientRef, ConsentNames.Of(consent.Purpose), consent.Version, consent.RevokedAt!.Value),
            now);
        db.RecordAccess(consent.TenantId, SourceService, actor, "consent.revoked", ResourceType, consent.PatientRef, now);
    }
}
