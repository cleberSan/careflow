namespace CareFlow.Contracts.Rights;

/// <summary>O que acontece com um item depois de um pedido de eliminação.</summary>
public static class RetentionActions
{
    /// <summary>Ilegível por crypto-shredding: a chave foi destruída.</summary>
    public const string Destroyed = "destroyed";

    /// <summary>Apagado ou sobrescrito.</summary>
    public const string Erased = "erased";

    /// <summary>Cancelado (agendamentos futuros, avisos pendentes).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Mantido pelo prazo do item, sem nenhum dado pessoal além do pseudônimo.</summary>
    public const string Retained = "retained";
}

/// <summary>Um item da política: de qual serviço, o que é, o que acontece, por quanto tempo fica e por quê.</summary>
public sealed record RetentionRule(string Item, string Service, string Description, string Action, int? RetainDays, string Basis)
{
    /// <summary>Até quando o item fica, ou nulo se não fica (ou se fica sem prazo).</summary>
    public DateTimeOffset? RetainedUntil(DateTimeOffset erasedAt) =>
        Action == RetentionActions.Retained && RetainDays is { } days ? erasedAt.AddDays(days) : null;
}

/// <summary>
/// Política de retenção depois da eliminação, fixa e versionada (decisão de Cleber, ADR-0012): igual para todas as
/// clínicas, e a versão vai no comprovante de cada eliminação. Mudar um prazo é uma versão nova, nunca uma edição
/// desta. Os prazos e bases legais são referência para a demonstração, não parecer jurídico.
/// </summary>
public static class RetentionPolicy
{
    public const string Version = "2026-10.1";

    /// <summary>5 anos: prescrição de referência para defesa em processo (LGPD art. 16, I).</summary>
    public const int FiveYears = 1826;

    /// <summary>20 anos: guarda de prontuário (Lei 13.787/2018, art. 6º).</summary>
    public const int TwentyYears = 7305;

    public static class Items
    {
        public const string VaultPersonalData = "vault.personal-data";
        public const string VaultCpfIndex = "vault.cpf-index";
        public const string VaultPseudonym = "vault.pseudonym";
        public const string SchedulingFutureAppointments = "scheduling.future-appointments";
        public const string SchedulingPastAppointments = "scheduling.past-appointments";
        public const string ConsentHistory = "consent.history";
        public const string NotificationsPending = "notifications.pending";
        public const string NotificationsProviderReference = "notifications.provider-reference";
        public const string NotificationsRecords = "notifications.records";
        public const string TriageText = "triage.text";
        public const string TriageDecision = "triage.decision";
        public const string AuditTrail = "audit.trail";
    }

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        new(Items.VaultPersonalData, SubjectRequestServices.Vault,
            "Nome, CPF, nascimento, e-mail e telefone no cofre", RetentionActions.Destroyed, null,
            "Crypto-shredding: a chave do paciente é destruída e o cifrado fica ilegível, inclusive em cópias"),
        new(Items.VaultCpfIndex, SubjectRequestServices.Vault,
            "Índice cego do CPF", RetentionActions.Erased, null,
            "Sem ele, o CPF não encontra mais o pseudônimo e pode ser cadastrado de novo"),
        new(Items.VaultPseudonym, SubjectRequestServices.Vault,
            "Pseudônimo e data da eliminação", RetentionActions.Retained, null,
            "Prova da eliminação; sozinho não identifica ninguém"),
        new(Items.SchedulingFutureAppointments, SubjectRequestServices.Scheduling,
            "Consultas futuras", RetentionActions.Cancelled, null,
            "Não há mais com quem falar sobre elas"),
        new(Items.SchedulingPastAppointments, SubjectRequestServices.Scheduling,
            "Consultas passadas (data, profissional e situação)", RetentionActions.Retained, FiveYears,
            "LGPD art. 16, I: defesa em processo (prazo de referência de 5 anos)"),
        new(Items.ConsentHistory, SubjectRequestServices.Consent,
            "Histórico de consentimentos (finalidade, base legal, versão do termo e datas)", RetentionActions.Retained, FiveYears,
            "LGPD art. 8º, §2º: cabe ao controlador provar o consentimento"),
        new(Items.NotificationsPending, SubjectRequestServices.Notifications,
            "Avisos ainda não enviados", RetentionActions.Cancelled, null,
            "O contato deixou de existir no cofre"),
        new(Items.NotificationsProviderReference, SubjectRequestServices.Notifications,
            "Identificador da mensagem no provedor", RetentionActions.Erased, null,
            "Leva ao destino e ao texto guardados pelo provedor"),
        new(Items.NotificationsRecords, SubjectRequestServices.Notifications,
            "Registro dos avisos (tipo, canal, situação e horários)", RetentionActions.Retained, FiveYears,
            "LGPD art. 16, I: defesa em processo (prazo de referência de 5 anos)"),
        new(Items.TriageText, SubjectRequestServices.Triage,
            "Texto da queixa, texto mascarado enviado ao modelo e justificativa do modelo", RetentionActions.Erased, null,
            "A auditoria guarda só o resumo SHA-256 do que saiu (ADR-0011)"),
        new(Items.TriageDecision, SubjectRequestServices.Triage,
            "Prioridade sugerida, sinais de alarme e decisão do profissional", RetentionActions.Retained, TwentyYears,
            "Lei 13.787/2018, art. 6º, se a clínica tratar a decisão como parte do prontuário"),
        new(Items.AuditTrail, SubjectRequestServices.Audit,
            "Trilha de auditoria (quem acessou o quê e quando, só com pseudônimos)", RetentionActions.Retained, null,
            "LGPD art. 37; a cadeia HMAC precisa continuar verificável (ADR-0007)"),
    ];

    public static RetentionRule Rule(string item) =>
        Rules.FirstOrDefault(rule => rule.Item == item)
        ?? throw new ArgumentOutOfRangeException(nameof(item), item, "Item fora da política de retenção.");

    public static IEnumerable<RetentionRule> For(string service) => Rules.Where(rule => rule.Service == service);
}
