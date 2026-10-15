namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

/// <summary>DEK de uma clínica, cifrada pela KEK do KMS. Nunca é gravada em claro.</summary>
internal sealed class TenantKeyRecord
{
    public Guid TenantId { get; init; }

    public int Version { get; init; }

    public required string KeyEncryptionKeyId { get; init; }

    public required byte[] WrappedKey { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
