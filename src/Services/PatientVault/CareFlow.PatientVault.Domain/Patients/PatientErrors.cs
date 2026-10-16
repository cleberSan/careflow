using CareFlow.BuildingBlocks.Results;

namespace CareFlow.PatientVault.Domain.Patients;

/// <summary>
/// Erros do cofre. As mensagens nunca repetem o valor recebido, porque podem chegar a logs e respostas.
/// </summary>
public static class PatientErrors
{
    public static readonly Error InvalidName = Error.Validation(
        "Paciente.NomeInvalido", "Informe o nome completo, entre 2 e 150 caracteres, sem caracteres de controle.");

    public static readonly Error InvalidCpf = Error.Validation(
        "Paciente.CpfInvalido", "O CPF informado não é válido.");

    public static readonly Error InvalidBirthDate = Error.Validation(
        "Paciente.DataNascimentoInvalida", "A data de nascimento não pode estar no futuro nem ter mais de 130 anos.");

    public static readonly Error InvalidEmail = Error.Validation(
        "Paciente.EmailInvalido", "O e-mail informado não é válido.");

    public static readonly Error InvalidPhone = Error.Validation(
        "Paciente.TelefoneInvalido", "O telefone deve ter de 10 a 13 dígitos, com DDD.");

    public static readonly Error NotFound = Error.NotFound(
        "Paciente.NaoEncontrado", "Paciente não encontrado.");

    public static readonly Error Erased = Error.Gone(
        "Paciente.Eliminado", "Os dados deste paciente foram eliminados a pedido do titular e não podem mais ser lidos.");

    public static readonly Error DuplicateCpf = Error.Conflict(
        "Paciente.CpfJaCadastrado", "Já existe um paciente com este CPF nesta clínica.");

    public static readonly Error VersionRequired = Error.PreconditionRequired(
        "Paciente.VersaoObrigatoria", "Envie o cabeçalho If-Match com o ETag lido do paciente.");

    public static readonly Error StaleVersion = Error.PreconditionFailed(
        "Paciente.VersaoDesatualizada", "O paciente foi alterado depois da sua leitura. Leia de novo e repita a alteração.");
}
