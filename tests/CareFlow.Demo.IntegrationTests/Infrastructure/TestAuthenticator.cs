using CareFlow.Demo.Api.Services;

namespace CareFlow.Demo.IntegrationTests.Infrastructure;

/// <summary>No lugar do token HS256: a clínica vai num cabeçalho, para o serviço de mentira saber de quem é a chamada.</summary>
internal sealed class TestAuthenticator : IServiceAuthenticator
{
    public const string ClinicHeader = "X-Test-Clinic";

    public void Authenticate(HttpRequestMessage request, ServiceCall call)
    {
        request.Headers.Remove(ClinicHeader);
        request.Headers.Add(ClinicHeader, call.Clinic.ToString("N"));
    }

    public void Forget(Guid clinic)
    {
    }
}
