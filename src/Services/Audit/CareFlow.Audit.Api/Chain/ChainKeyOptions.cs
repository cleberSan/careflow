using System.ComponentModel.DataAnnotations;

namespace CareFlow.Audit.Api.Chain;

/// <summary>
/// Chaves do HMAC da cadeia, em base64, por identificador. Ficam em user secrets no desenvolvimento e no
/// cofre de segredos da nuvem em produção, nunca no repositório nem no banco da auditoria.
/// </summary>
internal sealed class ChainKeyOptions
{
    public const string SectionName = "Audit:Chain";

    [Required]
    public string ActiveKeyId { get; set; } = string.Empty;

    /// <summary>Chaves antigas continuam aqui depois de uma rotação, para verificar as entradas que assinaram.</summary>
    public Dictionary<string, string> Keys { get; init; } = [];
}
