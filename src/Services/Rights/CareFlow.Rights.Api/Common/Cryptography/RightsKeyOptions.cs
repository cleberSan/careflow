using System.ComponentModel.DataAnnotations;

namespace CareFlow.Rights.Api.Common.Cryptography;

/// <summary>
/// Chave do serviço (AES-256, Base64): cifra a chave privada de cada pedido e a chave de cada pacote, e deriva a
/// chave do HMAC dos códigos. Fica em user secrets no desenvolvimento e no cofre de segredos em produção, nunca no
/// repositório nem no banco.
/// </summary>
internal sealed class RightsKeyOptions
{
    public const string SectionName = "Rights:Keys";

    [Required]
    public string KeyEncryptionKey { get; set; } = string.Empty;
}

/// <summary>Chave pública do Notifications (P-256, SPKI em Base64), para cifrar o código de confirmação.</summary>
internal sealed class IdentityVerificationOptions
{
    public const string SectionName = "IdentityVerification";

    [Required]
    public string NotificationsPublicKey { get; set; } = string.Empty;
}
