namespace CareFlow.Consent.Domain.Consents;

/// <summary>Finalidades do tratamento (LGPD art. 6º, I). Cada finalidade tem o seu próprio registro.</summary>
public enum ConsentPurpose
{
    /// <summary>Agendar e gerenciar consultas.</summary>
    Scheduling = 1,

    /// <summary>Enviar lembretes e avisos por WhatsApp, SMS ou e-mail (fase 5).</summary>
    Notifications,

    /// <summary>Usar sintomas pseudonimizados na triagem assistida por IA (fase 7).</summary>
    AiTriage,
}
