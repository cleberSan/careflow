using System.Buffers.Text;
using System.Security.Cryptography;

namespace CareFlow.Rights.Api.Features.Downloads;

/// <summary>
/// Token do link: a clínica (16 bytes, para o download anônimo saber em qual clínica procurar com RLS) e 32 bytes
/// aleatórios, em Base64url. Só o SHA-256 vai para o banco. A clínica não é segredo; o acaso de 256 bits é.
/// </summary>
internal static class DownloadToken
{
    private const int TenantSize = 16;
    private const int SecretSize = 32;

    public static (string Token, byte[] Hash) Create(Guid tenantId)
    {
        var raw = new byte[TenantSize + SecretSize];
        tenantId.TryWriteBytes(raw.AsSpan(0, TenantSize));
        RandomNumberGenerator.Fill(raw.AsSpan(TenantSize));
        try
        {
            return (Base64Url.EncodeToString(raw), SHA256.HashData(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public static bool TryParse(string? token, out Guid tenantId, out byte[] hash)
    {
        tenantId = Guid.Empty;
        hash = [];
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return false;
        }

        var raw = new byte[TenantSize + SecretSize];
        if (!Base64Url.IsValid(token, out var length) || length != raw.Length
            || Base64Url.DecodeFromChars(token, raw) != raw.Length)
        {
            return false;
        }

        tenantId = new Guid(raw.AsSpan(0, TenantSize));
        hash = SHA256.HashData(raw);
        CryptographicOperations.ZeroMemory(raw);
        return tenantId != Guid.Empty;
    }
}
