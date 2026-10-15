using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Consent.Domain.Consents;

/// <summary>Erros do consentimento. As mensagens nunca repetem o valor recebido.</summary>
public static class ConsentErrors
{
    public static readonly Error MissingIdentifier = Error.Validation(
        "Consentimento.IdentificadorAusente", "Clínica e paciente são obrigatórios.");

    public static readonly Error InvalidPurpose = Error.Validation(
        "Consentimento.FinalidadeInvalida", "Finalidade desconhecida.");

    public static readonly Error InvalidLegalBasis = Error.Validation(
        "Consentimento.BaseLegalInvalida", "Base legal desconhecida.");

    public static readonly Error InvalidTermVersion = Error.Validation(
        "Consentimento.VersaoDoTermoInvalida", "A versão do termo deve ter de 1 a 32 letras, dígitos, '.', '_' ou '-'.");

    public static readonly Error NotRevocable = Error.Conflict(
        "Consentimento.BaseLegalNaoRevogavel", "Esta base legal não depende de consentimento e não pode ser revogada pelo titular.");

    public static readonly Error NotFound = Error.NotFound(
        "Consentimento.NaoEncontrado", "Consentimento não encontrado.");

    public static readonly Error ConcurrentChange = Error.Conflict(
        "Consentimento.AlteracaoConcorrente", "O consentimento foi alterado por outra requisição. Tente novamente.");
}
