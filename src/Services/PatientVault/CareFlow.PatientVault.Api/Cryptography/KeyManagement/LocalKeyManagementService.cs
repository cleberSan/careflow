using System.Collections.Frozen;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

/// <summary>
/// KMS local para desenvolvimento e testes: AES-256-GCM com a KEK em memória. Mesmo contrato de um KMS
/// de nuvem (wrap/unwrap com contexto), então trocar de provedor não muda o restante do cofre.
/// Não deve ser usado em produção: a KEK fica no mesmo processo que os dados.
/// </summary>
public sealed class LocalKeyManagementService : IKeyManagementService
{
    public const int KeySizeBytes = 32;

    private readonly FrozenDictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    public LocalKeyManagementService(IOptions<LocalKeyManagementOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _keys = value.Keys.ToFrozenDictionary(pair => pair.Key, pair => Decode(pair.Key, pair.Value), StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value.ActiveKeyId) || !_keys.ContainsKey(value.ActiveKeyId))
        {
            throw new InvalidOperationException(
                $"{LocalKeyManagementOptions.SectionName}:ActiveKeyId precisa apontar para uma chave configurada em {LocalKeyManagementOptions.SectionName}:Keys.");
        }

        _activeKeyId = value.ActiveKeyId;
    }

    public Task<WrappedKey> WrapKeyAsync(ReadOnlyMemory<byte> dataKey, KeyWrapContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ciphertext = AeadEnvelope.Seal(_keys[_activeKeyId], dataKey.Span, context.ToAssociatedData());
        return Task.FromResult(new WrappedKey(_activeKeyId, ciphertext));
    }

    public Task<byte[]> UnwrapKeyAsync(WrappedKey wrappedKey, KeyWrapContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_keys.TryGetValue(wrappedKey.KeyEncryptionKeyId, out var kek))
        {
            throw new CryptographicException("A chave mestra que cifrou esta chave de dados não está configurada.");
        }

        return Task.FromResult(AeadEnvelope.Open(kek, wrappedKey.Ciphertext, context.ToAssociatedData()));
    }

    private static byte[] Decode(string keyId, string base64)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"A chave mestra '{keyId}' não está em Base64.");
        }

        return key.Length == KeySizeBytes
            ? key
            : throw new InvalidOperationException($"A chave mestra '{keyId}' precisa ter {KeySizeBytes} bytes.");
    }
}
