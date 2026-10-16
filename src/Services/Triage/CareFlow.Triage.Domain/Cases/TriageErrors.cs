using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Triage.Domain.Cases;

/// <summary>Erros da triagem. As mensagens nunca repetem a queixa nem dados do paciente.</summary>
public static class TriageErrors
{
    public static readonly Error MissingIdentifier = Error.Validation(
        "Triagem.IdentificadorAusente", "Clínica e paciente são obrigatórios.");

    public static readonly Error InvalidComplaint = Error.Validation(
        "Triagem.QueixaInvalida", "A queixa deve ter de 3 a 1000 caracteres.");

    public static readonly Error InvalidReviewer = Error.Validation(
        "Triagem.RevisorInvalido", "Revisor ausente ou inválido.");

    public static readonly Error InvalidDecision = Error.Validation(
        "Triagem.DecisaoInvalida", "A decisão deve ser 'confirm' ou 'correct'.");

    public static readonly Error InvalidPriority = Error.Validation(
        "Triagem.PrioridadeInvalida", "Prioridade desconhecida.");

    public static readonly Error NothingToConfirm = Error.Conflict(
        "Triagem.SemSugestaoParaConfirmar", "Não há sugestão para confirmar; informe a prioridade com a decisão 'correct'.");

    public static readonly Error CorrectionMustDiffer = Error.Validation(
        "Triagem.CorrecaoIgualASugestao", "A correção precisa de uma prioridade diferente da sugerida; para manter, confirme.");

    public static readonly Error AlreadyReviewed = Error.Conflict(
        "Triagem.JaRevisada", "Esta triagem já foi confirmada ou corrigida.");

    public static readonly Error NotFound = Error.NotFound(
        "Triagem.NaoEncontrada", "Triagem não encontrada.");

    public static readonly Error PatientUnknown = Error.NotFound(
        "Triagem.PacienteNaoEncontrado", "Paciente não encontrado no cofre.");

    public static readonly Error VaultUnavailable = Error.Failure(
        "Triagem.CofreIndisponivel", "Não foi possível ler a faixa etária do paciente agora. Tente novamente.");

    public static readonly Error ConcurrentChange = Error.Conflict(
        "Triagem.AlteracaoConcorrente", "A triagem foi alterada por outra requisição. Recarregue e tente de novo.");
}
