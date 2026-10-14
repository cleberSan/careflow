namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

/// <summary>
/// Linha do cofre. Todo dado pessoal é cifrado por campo; em claro ficam só identificadores técnicos,
/// a versão da chave e o blind index do CPF (HMAC, não reversível sem a chave da clínica).
/// </summary>
internal sealed class PatientRecord
{
    /// <summary>Pseudônimo público (UUID v4 aleatório, sem relação com os dados nem com a data de cadastro).</summary>
    public Guid PatientRef { get; init; }

    public Guid TenantId { get; init; }

    public int KeyVersion { get; set; }

    public required byte[] FullName { get; set; }

    public required byte[] Cpf { get; set; }

    public required byte[] BirthDate { get; set; }

    public byte[]? Email { get; set; }

    public byte[]? Phone { get; set; }

    public required byte[] CpfIndex { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Coluna de sistema <c>xmin</c>: concorrência otimista e ETag.</summary>
    public uint RowVersion { get; set; }
}
