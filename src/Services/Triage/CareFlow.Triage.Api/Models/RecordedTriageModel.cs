using System.Text.Json;

using CareFlow.Triage.Domain.Engine;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Reproduz respostas gravadas (<see cref="ModelCassette"/>), procuradas pelo digest do pedido. Sem gravação
/// para o pedido, o modelo fica "indisponível": a triagem segue só com as regras, e a avaliação do CI acusa a
/// falta. Nunca chama rede.
/// </summary>
internal sealed class RecordedTriageModel(string directory, string name) : ITriageModel
{
    public const string MissingReason = "sem-gravacao";

    public string Name { get; } = name;

    public async Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // O digest é hexadecimal de 64 caracteres (SHA-256): não há como montar um caminho fora do diretório.
        if (prompt.Digest.Length != 64 || !prompt.Digest.All(char.IsAsciiHexDigitLower))
        {
            return new ModelReply.Unavailable(MissingReason);
        }

        var path = Path.Combine(directory, ModelCassette.FileName(prompt.Digest));
        if (!File.Exists(path))
        {
            return new ModelReply.Unavailable(MissingReason);
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var cassette = await JsonSerializer.DeserializeAsync<ModelCassette>(stream, ModelCassette.Json, cancellationToken).ConfigureAwait(false);
            return cassette is null || !string.Equals(cassette.Digest, prompt.Digest, StringComparison.Ordinal)
                ? new ModelReply.Unavailable("gravacao-invalida")
                : cassette.ToReply();
        }
    }
}
