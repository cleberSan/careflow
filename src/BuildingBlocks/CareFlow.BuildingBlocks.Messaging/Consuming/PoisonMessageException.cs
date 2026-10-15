namespace CareFlow.BuildingBlocks.Messaging.Consuming;

/// <summary>
/// O conteúdo do evento nunca vai ser aceito (por exemplo, um campo fora do formato). Lançada pelo handler,
/// manda a mensagem direto para a fila de mensagens mortas em vez de gastar todas as entregas. A mensagem
/// da exceção não pode conter dados do evento.
/// </summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException()
    {
    }

    public PoisonMessageException(string message)
        : base(message)
    {
    }

    public PoisonMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
