using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Pseudonymization;
using CareFlow.Triage.Domain.Safety;
using CareFlow.Triage.Domain.Suggestions;

namespace CareFlow.Triage.Domain.Engine;

/// <summary>
/// A triagem automática, sem banco nem HTTP, para ser testada e avaliada igual em produção e no CI:
/// <list type="number">
/// <item>sinais de alarme por regra, sobre o texto original (sempre, mesmo sem IA);</item>
/// <item>sem consentimento ai-triage, para aqui;</item>
/// <item>sinais de prompt injection, para aqui;</item>
/// <item>pseudonimização; se sobrar pouco além de marcadores, para aqui;</item>
/// <item>chamada ao modelo, validação do schema e da linguagem clínica;</item>
/// <item>prioridade final = maior entre a do modelo e o piso dos alarmes.</item>
/// </list>
/// </summary>
public static class TriageEngine
{
    public const int MaxComplaintLength = 1000;

    /// <summary>Acima desta fração de texto mascarado, a queixa não vai ao modelo.</summary>
    public const double MaxMaskedShare = 0.5;

    public static async Task<TriageAssessment> AssessAsync(TriageInput input, ITriageModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);

        var alarms = AlarmSignRules.Evaluate(input.Complaint, input.AgeBand);
        var guardrails = alarms.Select(a => new Guardrail(Guardrail.Alarm, a.Code)).ToList();

        if (!input.ConsentGranted)
        {
            guardrails.Add(new Guardrail(Guardrail.Consent, "sem-consentimento-ai-triage"));
            return RulesOnly(ModelStatus.SkippedNoConsent, alarms, guardrails, masked: null, prompt: null, modelName: null);
        }

        var injection = InjectionGuard.Inspect(input.Complaint);
        if (injection.Count > 0)
        {
            guardrails.AddRange(injection.Select(code => new Guardrail(Guardrail.Injection, code)));
            return RulesOnly(ModelStatus.SkippedInjection, alarms, guardrails, masked: null, prompt: null, modelName: null);
        }

        var masked = ComplaintMasker.Mask(input.Complaint, input.Identity);
        if (masked.MaskedShare > MaxMaskedShare)
        {
            guardrails.Add(new Guardrail(Guardrail.Masking, "mascaramento-excessivo"));
            return RulesOnly(ModelStatus.SkippedOverMasked, alarms, guardrails, masked, prompt: null, modelName: null);
        }

        var prompt = TriagePrompt.Build(input.AgeBand, masked.Text);
        var reply = await model.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
        switch (reply)
        {
            case ModelReply.Unavailable unavailable:
                guardrails.Add(new Guardrail(Guardrail.Provider, unavailable.Reason));
                return RulesOnly(ModelStatus.Unavailable, alarms, guardrails, masked, prompt, model.Name);
            case ModelReply.Refused:
                guardrails.Add(new Guardrail(Guardrail.Provider, "recusa"));
                return RulesOnly(ModelStatus.Refused, alarms, guardrails, masked, prompt, model.Name);
        }

        var parsed = SuggestionSchema.Parse(((ModelReply.Completed)reply).Text);
        if (parsed.IsFailure)
        {
            guardrails.Add(new Guardrail(Guardrail.Schema, parsed.Error.Code));
            return RulesOnly(ModelStatus.RejectedSchema, alarms, guardrails, masked, prompt, model.Name);
        }

        var suggestion = parsed.Value;
        var clinical = ClinicalLanguageGuard.Inspect(suggestion.Rationale);
        if (clinical.Count > 0)
        {
            guardrails.AddRange(clinical.Select(code => new Guardrail(Guardrail.ClinicalLanguage, code)));
            return RulesOnly(ModelStatus.RejectedClinicalLanguage, alarms, guardrails, masked, prompt, model.Name, suggestion.Priority);
        }

        var floor = AlarmSignRules.FloorOf(alarms);
        var raised = floor is { } f && f > suggestion.Priority;
        if (raised)
        {
            guardrails.Add(new Guardrail(Guardrail.Alarm, "prioridade-elevada-por-regra"));
        }

        return new TriageAssessment(
            raised ? floor : suggestion.Priority,
            raised ? SuggestionSource.ModelRaisedByRules : SuggestionSource.Model,
            suggestion.Rationale,
            ModelStatus.Accepted,
            suggestion.Priority,
            alarms,
            guardrails,
            masked,
            prompt,
            model.Name);
    }

    /// <summary>Justificativa fixa quando só as regras sustentam a prioridade.</summary>
    public static string RulesRationale(IEnumerable<AlarmSign> alarms) =>
        $"Sinais de alarme na queixa ({string.Join(", ", alarms.Select(a => a.Code))}). Prioridade mínima definida por regra; aguarda o profissional.";

    private static TriageAssessment RulesOnly(
        ModelStatus status,
        IReadOnlyList<AlarmSign> alarms,
        List<Guardrail> guardrails,
        MaskedComplaint? masked,
        ModelPrompt? prompt,
        string? modelName,
        TriagePriority? discardedModelPriority = null)
    {
        var floor = AlarmSignRules.FloorOf(alarms);
        return new TriageAssessment(
            floor,
            floor is null ? SuggestionSource.None : SuggestionSource.Rules,
            floor is null ? null : RulesRationale(alarms),
            status,
            discardedModelPriority,
            alarms,
            guardrails,
            masked,
            prompt,
            modelName);
    }
}

/// <summary>Entrada da triagem: a queixa já limpa, a faixa etária e o que o cofre sabe do nome.</summary>
public sealed record TriageInput(string Complaint, AgeBand AgeBand, KnownIdentity Identity, bool ConsentGranted)
{
    public override string ToString() => "TriageInput { *** }";
}
