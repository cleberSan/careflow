using CareFlow.Audit.Domain.Chain;

using Microsoft.Extensions.Options;

namespace CareFlow.Audit.Api.Chain;

/// <summary>
/// Decodifica e valida as chaves uma vez, na subida. Uma chave ausente ou curta derruba o processo cedo,
/// e as mensagens de erro nunca incluem o valor da chave.
/// </summary>
internal sealed class ChainKeyRing
{
    private readonly Dictionary<string, ChainKey> _keys;

    public ChainKeyRing(IOptions<ChainKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        _keys = new Dictionary<string, ChainKey>(StringComparer.Ordinal);
        foreach (var (id, encoded) in value.Keys)
        {
            _keys[id] = Decode(id, encoded);
        }

        if (string.IsNullOrWhiteSpace(value.ActiveKeyId) || !_keys.TryGetValue(value.ActiveKeyId, out var active))
        {
            throw new InvalidOperationException(
                $"Configure {ChainKeyOptions.SectionName}:ActiveKeyId e a chave correspondente em {ChainKeyOptions.SectionName}:Keys.");
        }

        Active = active;
    }

    public ChainKey Active { get; }

    public ChainKey? Find(string id) => _keys.GetValueOrDefault(id);

    private static ChainKey Decode(string id, string encoded)
    {
        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"A chave da cadeia '{id}' não está em base64.");
        }

        if (secret.Length < ChainKey.MinimumKeySize)
        {
            throw new InvalidOperationException(
                $"A chave da cadeia '{id}' precisa de pelo menos {ChainKey.MinimumKeySize} bytes.");
        }

        return new ChainKey(id, secret);
    }
}
