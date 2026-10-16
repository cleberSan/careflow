using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Rights.Domain.Requests;

/// <summary>Erros dos pedidos do titular. As mensagens nunca repetem o valor recebido.</summary>
public static class SubjectRequestErrors
{
    public static readonly Error MissingIdentifier = Error.Validation(
        "Direitos.IdentificadorAusente", "Clínica e paciente são obrigatórios.");

    public static readonly Error InvalidKind = Error.Validation(
        "Direitos.TipoInvalido", "Tipo de pedido desconhecido. Use access, portability ou erasure.");

    public static readonly Error NotFound = Error.NotFound(
        "Direitos.PedidoNaoEncontrado", "Pedido não encontrado.");

    public static readonly Error AlreadyOpen = Error.Conflict(
        "Direitos.PedidoEmAberto", "Já existe um pedido deste tipo em aberto para o paciente.");

    public static readonly Error NotAwaitingIdentity = Error.Conflict(
        "Direitos.IdentidadeNaoPendente", "O pedido não está aguardando a confirmação de identidade.");

    public static readonly Error InvalidCode = Error.Validation(
        "Direitos.CodigoInvalido", "Código incorreto.");

    public static readonly Error CodeExpired = Error.Conflict(
        "Direitos.CodigoExpirado", "O código expirou e o pedido foi negado. Abra um novo pedido.");

    public static readonly Error AttemptsExhausted = Error.Conflict(
        "Direitos.TentativasEsgotadas", "As tentativas acabaram e o pedido foi negado. Abra um novo pedido.");

    public static readonly Error PackageUnavailable = Error.Conflict(
        "Direitos.PacoteIndisponivel", "O pacote não existe ou já expirou. Abra um novo pedido de acesso.");

    public static readonly Error NotACompletedErasure = Error.Conflict(
        "Direitos.NaoEhEliminacaoConcluida", "Só uma eliminação concluída pode ser reaplicada.");

    public static readonly Error ConcurrentChange = Error.Conflict(
        "Direitos.AlteracaoConcorrente", "O pedido foi alterado por outra requisição. Tente novamente.");
}
