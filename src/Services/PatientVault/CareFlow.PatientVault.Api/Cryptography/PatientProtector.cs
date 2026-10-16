using System.Globalization;

using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;

namespace CareFlow.PatientVault.Api.Cryptography;

/// <summary>Converte dados pessoais validados em linha cifrada e vice-versa. Cada gravação usa nonces novos.</summary>
internal static class PatientProtector
{
    public const string FullNameField = "full_name";
    public const string CpfField = "cpf";
    public const string BirthDateField = "birth_date";
    public const string EmailField = "email";
    public const string PhoneField = "phone";

    public static PatientRecord Protect(PersonalData data, PatientDataKey fieldKey, TenantDataKey indexKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(fieldKey);
        ArgumentNullException.ThrowIfNull(indexKey);

        var record = new PatientRecord
        {
            PatientRef = fieldKey.PatientRef,
            TenantId = fieldKey.TenantId,
            CreatedAt = now,
            FullName = [],
            Cpf = [],
            BirthDate = [],
            CpfIndex = [],
        };

        Apply(record, data, fieldKey, indexKey, now);
        return record;
    }

    /// <summary>
    /// Recifra todos os campos com a chave do paciente e recalcula o blind index com a chave atual da clínica.
    /// Também migra um registro do esquema antigo (chave da clínica) para a chave do paciente.
    /// </summary>
    public static void Apply(PatientRecord record, PersonalData data, PatientDataKey fieldKey, TenantDataKey indexKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(fieldKey);
        ArgumentNullException.ThrowIfNull(indexKey);

        Encrypt(record, data, fieldKey);
        record.KeyScheme = KeyScheme.PatientKey;
        record.KeyVersion = indexKey.Version;
        record.CpfIndex = IndexCpf(data.Cpf, indexKey);
        record.UpdatedAt = now;
    }

    /// <summary>Cifra os campos com uma chave qualquer, sem mexer no esquema nem no índice.</summary>
    public static void Encrypt(PatientRecord record, PersonalData data, IFieldCipher key)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(key);

        var patientRef = record.PatientRef;
        record.FullName = key.Encrypt(data.FullName, new FieldContext(patientRef, FullNameField));
        record.Cpf = key.Encrypt(data.Cpf.Digits, new FieldContext(patientRef, CpfField));
        record.BirthDate = key.Encrypt(data.BirthDate.ToString("O", CultureInfo.InvariantCulture), new FieldContext(patientRef, BirthDateField));
        record.Email = data.Email is null ? null : key.Encrypt(data.Email, new FieldContext(patientRef, EmailField));
        record.Phone = data.Phone is null ? null : key.Encrypt(data.Phone, new FieldContext(patientRef, PhoneField));
    }

    public static byte[] IndexCpf(Cpf cpf, TenantDataKey key)
    {
        ArgumentNullException.ThrowIfNull(cpf);
        ArgumentNullException.ThrowIfNull(key);
        return key.BlindIndex(cpf.Digits);
    }

    /// <summary>Decifra a linha. Falha de integridade lança <see cref="System.Security.Cryptography.CryptographicException"/>.</summary>
    public static DecryptedPatient Unprotect(PatientRecord record, IFieldCipher key)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(key);

        var patientRef = record.PatientRef;
        return new DecryptedPatient(
            key.Decrypt(record.FullName, new FieldContext(patientRef, FullNameField)),
            key.Decrypt(record.Cpf, new FieldContext(patientRef, CpfField)),
            DateOnly.ParseExact(key.Decrypt(record.BirthDate, new FieldContext(patientRef, BirthDateField)), "O", CultureInfo.InvariantCulture),
            record.Email is null ? null : key.Decrypt(record.Email, new FieldContext(patientRef, EmailField)),
            record.Phone is null ? null : key.Decrypt(record.Phone, new FieldContext(patientRef, PhoneField)));
    }
}

/// <summary>Dados decifrados como estão gravados (já validados na escrita).</summary>
internal sealed record DecryptedPatient(string FullName, string Cpf, DateOnly BirthDate, string? Email, string? Phone)
{
    public override string ToString() => "DecryptedPatient { *** }";

    /// <summary>Volta para o tipo validado, para recifrar (migração e rotação). Os dados já passaram pela validação na escrita.</summary>
    public PersonalData ToPersonalData(DateOnly today) =>
        PersonalData.Create(FullName, Domain.Patients.Cpf.Create(Cpf).Value, BirthDate, Email, Phone, today).Value;
}
