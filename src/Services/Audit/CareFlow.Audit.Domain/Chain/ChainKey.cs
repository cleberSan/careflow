namespace CareFlow.Audit.Domain.Chain;

/// <summary>
/// Chave do HMAC da cadeia. Fica fora do banco (Key Vault/KMS em produção), então quem só tem acesso de escrita
/// ao banco não consegue recalcular os hashes de uma cadeia adulterada. <see cref="ToString"/> não expõe o segredo.
/// </summary>
public sealed class ChainKey
{
    public const int MinimumKeySize = 32;

    private readonly byte[] _secret;

    public ChainKey(string id, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (secret.Length < MinimumKeySize)
        {
            throw new ArgumentException($"A chave da cadeia precisa de pelo menos {MinimumKeySize} bytes.", nameof(secret));
        }

        Id = id;
        _secret = secret.ToArray();
    }

    public string Id { get; }

    internal ReadOnlySpan<byte> Secret => _secret;

    public override string ToString() => $"ChainKey({Id})";
}
