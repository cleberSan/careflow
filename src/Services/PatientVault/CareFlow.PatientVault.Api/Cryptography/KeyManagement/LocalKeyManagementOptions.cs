namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

/// <summary>
/// KEKs locais, em Base64 (32 bytes). Vêm de user-secrets ou variáveis de ambiente, nunca do repositório.
/// Várias chaves podem coexistir: a ativa cifra, as demais só decifram (rotação da KEK).
/// </summary>
public sealed class LocalKeyManagementOptions
{
    public const string SectionName = "KeyManagement:Local";

    public string ActiveKeyId { get; set; } = string.Empty;

    public Dictionary<string, string> Keys { get; init; } = [];
}
