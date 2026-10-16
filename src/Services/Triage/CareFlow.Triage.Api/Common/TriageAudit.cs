using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Engine;

namespace CareFlow.Triage.Api.Common;

/// <summary>
/// Como a triagem aparece na trilha de auditoria. O recurso é o paciente (pseudônimo); a ação diz o que
/// aconteceu e de onde veio a sugestão. Nada da queixa vai para a trilha: quando a queixa mascarada sai para o
/// modelo, a trilha recebe só o SHA-256 do pedido exato (<see cref="TriageCase.PromptDigest"/>), coberto pela cadeia
/// HMAC; o texto fica no banco do Triage, que pode apagá-lo a pedido do titular (ADR-0011).
/// </summary>
internal static class TriageAudit
{
    public const string SourceService = "triage";
    public const string ResourceType = "patient";

    public const string Read = "triage.read";
    public const string Confirmed = "triage.confirmed";
    public const string Corrected = "triage.corrected";

    /// <summary><c>triage.suggested.model</c>, <c>.model-raised</c>, <c>.rules</c> ou <c>.none</c>.</summary>
    public static string Suggested(SuggestionSource source) => source switch
    {
        SuggestionSource.Model => "triage.suggested.model",
        SuggestionSource.ModelRaisedByRules => "triage.suggested.model-raised",
        SuggestionSource.Rules => "triage.suggested.rules",
        _ => "triage.suggested.none",
    };

    public static string Reviewed(ReviewDecision decision) => decision == ReviewDecision.Confirmed ? Confirmed : Corrected;

    public static void Record(this TriageDbContext db, TriageCase triage, string actor, string action, DateTimeOffset now) =>
        db.RecordAccess(triage.TenantId, SourceService, actor, action, ResourceType, triage.PatientRef, now);

    /// <summary>A sugestão, com o resumo do que saiu para o modelo quando algo saiu.</summary>
    public static void RecordSuggestion(this TriageDbContext db, TriageCase triage, string actor, DateTimeOffset now)
    {
        var action = Suggested(triage.Source);
        if (triage.PromptDigest is { } digest)
        {
            db.RecordDisclosure(triage.TenantId, SourceService, actor, action, ResourceType, triage.PatientRef, digest, now);
        }
        else
        {
            db.Record(triage, actor, action, now);
        }
    }
}
