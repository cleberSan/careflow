using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Audit.Domain.Chain;

/// <summary>Erros da auditoria. As mensagens nunca repetem o valor recebido.</summary>
public static class AuditErrors
{
    public static readonly Error MissingIdentifier = Error.Validation(
        "Auditoria.IdentificadorAusente", "Clínica, mensagem e recurso são obrigatórios.");

    public static readonly Error InvalidCode = Error.Validation(
        "Auditoria.CodigoInvalido", "Serviço, ação e tipo de recurso devem ter de 1 a 64 caracteres minúsculos, dígitos, '.', '_' ou '-'.");

    public static readonly Error InvalidActor = Error.Validation(
        "Auditoria.AtorInvalido", "O ator deve ter de 1 a 128 caracteres visíveis, sem espaços.");
}
