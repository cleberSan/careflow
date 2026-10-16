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

    /// <summary>
    /// Versão da chave da clínica usada no blind index do CPF (e, no esquema antigo, também nos campos). Chave
    /// estrangeira para <c>tenant_keys</c>: uma versão em uso não pode ser destruída.
    /// </summary>
    public int KeyVersion { get; set; }

    /// <summary>Com que chave os campos estão cifrados: a da clínica (fase 3) ou a do paciente (fase 8).</summary>
    public KeyScheme KeyScheme { get; set; } = KeyScheme.PatientKey;

    public required byte[] FullName { get; set; }

    public required byte[] Cpf { get; set; }

    public required byte[] BirthDate { get; set; }

    public byte[]? Email { get; set; }

    public byte[]? Phone { get; set; }

    /// <summary>Nulo depois da eliminação: o CPF deixa de encontrar o pseudônimo.</summary>
    public byte[]? CpfIndex { get; set; }

    /// <summary>Quando o titular foi eliminado (chave destruída). Nulo enquanto o paciente existe.</summary>
    public DateTimeOffset? ErasedAt { get; set; }

    public bool IsErased => ErasedAt is not null;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Coluna de sistema <c>xmin</c>: concorrência otimista e ETag.</summary>
    public uint RowVersion { get; set; }
}

/// <summary>Esquema de cifragem dos campos de um registro.</summary>
internal enum KeyScheme : short
{
    /// <summary>Campos cifrados com a chave da clínica (registros anteriores à fase 8, até a migração).</summary>
    ClinicKey = 1,

    /// <summary>Campos cifrados com a chave do próprio paciente, que pode ser destruída sozinha.</summary>
    PatientKey = 2,
}
