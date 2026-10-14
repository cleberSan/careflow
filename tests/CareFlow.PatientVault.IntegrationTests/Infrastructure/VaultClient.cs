using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.Testing;

namespace CareFlow.PatientVault.IntegrationTests.Infrastructure;

internal static class VaultClient
{
    public const string Read = "vault.read";
    public const string Write = "vault.write";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static RegisterPatientRequest NewPatient(string? cpf = null) =>
        new(SyntheticPatients.FullName(), cpf ?? SyntheticPatients.Cpf(), new DateOnly(1988, 3, 9), SyntheticPatients.Email(), SyntheticPatients.Phone());

    public static Task<HttpResponseMessage> RegisterAsync(this HttpClient client, RegisterPatientRequest request) =>
        client.PostAsJsonAsync("/v1/patients", request, Json, Ct);

    /// <summary>Cadastra e devolve o pseudônimo e o ETag.</summary>
    public static async Task<(Guid PatientRef, string ETag)> RegisterOkAsync(this HttpClient client, RegisterPatientRequest request)
    {
        var response = await client.RegisterAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PatientRefResponse>(Json, Ct);
        return (body!.PatientRef, response.Headers.ETag!.Tag);
    }

    public static Task<HttpResponseMessage> UpdateAsync(this HttpClient client, Guid patientRef, UpdatePatientRequest request, string? ifMatch)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, $"/v1/patients/{patientRef}")
        {
            Content = JsonContent.Create(request, options: Json),
        };
        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(message, Ct);
    }

    public static Task<HttpResponseMessage> LookupAsync(this HttpClient client, string? cpf) =>
        client.PostAsJsonAsync("/v1/patients/lookup", new LookupPatientRequest(cpf), Json, Ct);

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
