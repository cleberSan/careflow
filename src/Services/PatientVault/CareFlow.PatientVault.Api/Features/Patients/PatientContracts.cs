using CareFlow.PatientVault.Api.Cryptography;

namespace CareFlow.PatientVault.Api.Features.Patients;

public sealed record RegisterPatientRequest(string? FullName, string? Cpf, DateOnly BirthDate, string? Email, string? Phone)
{
    public override string ToString() => "RegisterPatientRequest { *** }";
}

public sealed record UpdatePatientRequest(string? FullName, DateOnly BirthDate, string? Email, string? Phone)
{
    public override string ToString() => "UpdatePatientRequest { *** }";
}

public sealed record LookupPatientRequest(string? Cpf)
{
    public override string ToString() => "LookupPatientRequest { *** }";
}

/// <summary>Só o pseudônimo: é tudo que os outros serviços precisam guardar.</summary>
public sealed record PatientRefResponse(Guid PatientRef);

public sealed record PatientResponse(
    Guid PatientRef,
    string FullName,
    string Cpf,
    DateOnly BirthDate,
    string? Email,
    string? Phone,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static PatientResponse From(Guid patientRef, DecryptedPatient patient, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        new(patientRef, patient.FullName, patient.Cpf, patient.BirthDate, patient.Email, patient.Phone, createdAt, updatedAt);

    public override string ToString() => $"PatientResponse {{ PatientRef = {PatientRef}, *** }}";
}

/// <summary>O mínimo para avisar o paciente.</summary>
public sealed record PatientContactResponse(string? Email, string? Phone)
{
    public override string ToString() => "PatientContactResponse { *** }";
}
