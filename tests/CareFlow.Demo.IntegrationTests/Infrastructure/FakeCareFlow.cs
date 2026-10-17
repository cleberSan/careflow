using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace CareFlow.Demo.IntegrationTests.Infrastructure;

/// <summary>
/// Os sete serviços do CareFlow de mentira, atrás de um handler só: respondem o mínimo que o Demo precisa para abrir
/// e apagar uma sessão (partição, paciente, consentimentos, limpeza) e registram cada chamada.
/// </summary>
internal sealed class FakeCareFlow : HttpMessageHandler
{
    public ConcurrentQueue<(string Service, HttpMethod Method, string Path)> Calls { get; } = new();

    /// <summary>Com falha ligada, a limpeza responde 503 (o serviço caiu).</summary>
    public bool PurgeFails { get; set; }

    public IReadOnlyList<string> PurgedServices(Guid clinic) =>
        [.. Calls.Where(call => call.Method == HttpMethod.Delete && call.Path == "/v1/demo/clinic-data" && call.Service.EndsWith(clinic.ToString("N"), StringComparison.Ordinal))
            .Select(call => call.Service.Split('#')[0])];

    public int PurgeCount => Calls.Count(call => call.Method == HttpMethod.Delete && call.Path == "/v1/demo/clinic-data");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var service = request.RequestUri!.Host.Split('.')[0];
        var clinic = request.Headers.TryGetValues(TestAuthenticator.ClinicHeader, out var values) ? values.Single() : "?";
        var path = request.RequestUri.AbsolutePath;
        Calls.Enqueue(($"{service}#{clinic}", request.Method, path));

        var response = (service, request.Method.Method, path) switch
        {
            ("notifications", "PUT", "/v1/fault-injection/partition") => new HttpResponseMessage(HttpStatusCode.OK),
            ("vault", "POST", "/v1/patients") => Json(HttpStatusCode.Created, new { patientRef = Guid.NewGuid() }),
            ("consent", "PUT", _) => new HttpResponseMessage(HttpStatusCode.OK),
            (_, "DELETE", "/v1/demo/clinic-data") when PurgeFails => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            (_, "DELETE", "/v1/demo/clinic-data") => Json(HttpStatusCode.OK, new { rows = 1 }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };
}
