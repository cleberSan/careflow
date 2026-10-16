namespace CareFlow.Triage.Domain.Engine;

/// <summary>
/// O que sai do serviço para o modelo: instruções fixas e versionadas, mais o caso pseudonimizado como JSON.
/// <see cref="Digest"/> é o SHA-256 dos dois; vai para a trilha de auditoria e identifica a resposta gravada.
/// </summary>
public sealed record ModelPrompt(string Version, string Instructions, string Payload, string Digest);

/// <summary>Resposta do provedor, ainda não validada.</summary>
public abstract record ModelReply
{
    private ModelReply()
    {
    }

    public sealed record Completed(string Text) : ModelReply;

    public sealed record Refused : ModelReply;

    public sealed record Unavailable(string Reason) : ModelReply;
}

/// <summary>Porta para o modelo de linguagem. Implementações: falsa determinística, respostas gravadas e o provedor real.</summary>
public interface ITriageModel
{
    /// <summary>Nome estável do modelo, registrado com cada sugestão.</summary>
    string Name { get; }

    Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken);
}
