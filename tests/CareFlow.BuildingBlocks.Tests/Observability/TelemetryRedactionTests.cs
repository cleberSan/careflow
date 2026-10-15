using CareFlow.BuildingBlocks.AspNetCore.Observability;

namespace CareFlow.BuildingBlocks.Tests.Observability;

public sealed class TelemetryRedactionTests
{
    [Theory]
    [InlineData("http://vault/v1/patients/3f2a9c1e-1b2c-4d5e-8f90-123456789abc/contact", "http://vault/v1/patients/{id}/contact")]
    [InlineData("/v1/audit/entries?resourceId=3f2a9c1e-1b2c-4d5e-8f90-123456789abc&limit=10", "/v1/audit/entries")]
    [InlineData("/v1/patients/3f2a9c1e1b2c4d5e8f90123456789abc", "/v1/patients/{id}")]
    [InlineData("/health/ready", "/health/ready")]
    public void MaskUrl_ReplacesIdentifiers_AndDropsQuery(string input, string expected) =>
        Assert.Equal(expected, TelemetryRedaction.MaskUrl(input));

    [Theory]
    [InlineData("contato sintetico.ab12@example.com recusado", "contato [email] recusado")]
    [InlineData("CPF 123.456.789-09 duplicado", "CPF [cpf] duplicado")]
    [InlineData("cpf=12345678909;", "cpf=[cpf];")]
    [InlineData("tel 5511912345678 fora", "tel [telefone] fora")]
    [InlineData("tel +55 (11) 91234-5678 fora", "tel [telefone] fora")]
    public void ScrubPersonalData_ReplacesEmailCpfAndPhone(string input, string expected) =>
        Assert.Equal(expected, TelemetryRedaction.ScrubPersonalData(input));

    [Theory]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("3f2a9c1e-1b2c-4d5e-8f90-123456789abc")]
    [InlineData("Auditoria: entrada 12345 gravada")]
    [InlineData("SELECT count(*) FROM outbox_messages WHERE dispatched_at IS NULL")]
    [InlineData("1760536800123")]
    [InlineData("curto")]
    public void ScrubPersonalData_LeavesOrdinaryValuesAlone(string input) =>
        Assert.Equal(input, TelemetryRedaction.ScrubPersonalData(input));

    [Fact]
    public void MaskIdentifiers_KeepsTheRest() =>
        Assert.Equal("Aviso {id} enviado", TelemetryRedaction.MaskIdentifiers("Aviso 3f2a9c1e-1b2c-4d5e-8f90-123456789abc enviado"));
}
