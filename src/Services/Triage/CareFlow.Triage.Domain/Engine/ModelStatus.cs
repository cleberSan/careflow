namespace CareFlow.Triage.Domain.Engine;

/// <summary>O que aconteceu com o modelo nesta triagem. Só <see cref="Accepted"/> usa a resposta dele.</summary>
public enum ModelStatus
{
    Accepted = 1,

    /// <summary>Sem consentimento ai-triage ativo: a queixa nem foi pseudonimizada para a IA.</summary>
    SkippedNoConsent,

    /// <summary>A queixa tinha sinais de prompt injection.</summary>
    SkippedInjection,

    /// <summary>Depois do mascaramento sobrou pouco além de marcadores.</summary>
    SkippedOverMasked,

    /// <summary>Provedor fora do ar, lento demais ou com o circuito aberto.</summary>
    Unavailable,

    /// <summary>O provedor se recusou a responder.</summary>
    Refused,

    /// <summary>Resposta fora do schema.</summary>
    RejectedSchema,

    /// <summary>Justificativa com diagnóstico, prescrição ou promessa clínica.</summary>
    RejectedClinicalLanguage,
}

/// <summary>De onde veio a prioridade sugerida.</summary>
public enum SuggestionSource
{
    /// <summary>Sem sugestão: o profissional define a prioridade.</summary>
    None = 0,

    /// <summary>Prioridade do modelo, sem alarme acima dela.</summary>
    Model,

    /// <summary>O modelo sugeriu menos do que um sinal de alarme exige; vale o piso da regra.</summary>
    ModelRaisedByRules,

    /// <summary>Só as regras locais (modelo não chamado ou descartado).</summary>
    Rules,
}
