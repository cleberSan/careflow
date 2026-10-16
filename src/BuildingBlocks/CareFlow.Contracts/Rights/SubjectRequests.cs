namespace CareFlow.Contracts.Rights;

/// <summary>Tipos de pedido do titular (LGPD art. 18, II, V e VI), como aparecem nos eventos.</summary>
public static class SubjectRequestKinds
{
    public const string Access = "access";
    public const string Portability = "portability";
    public const string Erasure = "erasure";

    public static IReadOnlyList<string> All { get; } = [Access, Portability, Erasure];

    public static bool IsKnown(string? kind) => kind is Access or Portability or Erasure;

    /// <summary>Acesso e portabilidade geram o pacote; eliminação gera o comprovante.</summary>
    public static bool IsExport(string? kind) => kind is Access or Portability;
}

/// <summary>
/// Serviços que guardam algo do paciente e precisam responder a todo pedido. O pedido só conclui quando todos
/// responderam; um serviço novo que guarde dado do paciente entra aqui e no consumidor dele.
/// </summary>
public static class SubjectRequestServices
{
    public const string Vault = "patient-vault";
    public const string Scheduling = "scheduling";
    public const string Consent = "consent";
    public const string Notifications = "notifications";
    public const string Triage = "triage";
    public const string Audit = "audit";

    public static IReadOnlyList<string> All { get; } = [Vault, Scheduling, Consent, Notifications, Triage, Audit];
}

/// <summary>Resultado da resposta de um serviço.</summary>
public static class SubjectRequestOutcomes
{
    /// <summary>A seção do serviço foi cifrada para o pedido e vai no evento.</summary>
    public const string Exported = "exported";

    /// <summary>A eliminação foi aplicada; o comprovante lista o que foi apagado e o que ficou.</summary>
    public const string Erased = "erased";

    /// <summary>O cofre não conhece o paciente nesta clínica: o pedido é negado.</summary>
    public const string SubjectNotFound = "subject-not-found";
}

/// <summary>
/// Contexto autenticado das seções cifradas: amarra o envelope ao pedido e ao serviço, então uma seção não pode ser
/// copiada para outro pedido nem trocada entre serviços.
/// </summary>
public static class SubjectRequestSealing
{
    public static string SectionContext(Guid requestId, string service) => $"careflow:rights:section:v1|{requestId:N}|{service}";

    public static string VerificationCodeContext(Guid requestId) => $"careflow:rights:verification-code:v1|{requestId:N}";
}
