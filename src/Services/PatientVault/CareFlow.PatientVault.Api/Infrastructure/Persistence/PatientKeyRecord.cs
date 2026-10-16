namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

/// <summary>
/// DEK de um paciente, embrulhada pela chave da clínica (<see cref="TenantKeyVersion"/>). É a única cópia da chave:
/// apagar a linha torna ilegíveis todos os campos do paciente (crypto-shredding, ADR-0012).
/// </summary>
internal sealed class PatientKeyRecord
{
    public Guid TenantId { get; init; }

    public Guid PatientRef { get; init; }

    public int TenantKeyVersion { get; set; }

    public required byte[] WrappedKey { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
