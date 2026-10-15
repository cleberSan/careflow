using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Triage.Api.Features.Triages;

namespace CareFlow.Triage.IntegrationTests.Infrastructure;

internal static class TriageClient
{
    public const string Submit = "triage.submit";
    public const string Read = "triage.read";
    public const string Review = "triage.review";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> SubmitAsync(this HttpClient client, Guid patientRef, string? complaint) =>
        client.PostAsJsonAsync("/v1/triages", new SubmitTriageRequest(patientRef, complaint), Ct);

    public static async Task<TriageResponse> SubmitOkAsync(this HttpClient client, Guid patientRef, string complaint)
    {
        var response = await client.SubmitAsync(patientRef, complaint);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TriageResponse>(JsonSerializerOptions.Web, Ct))!;
    }

    public static async Task<TriageResponse> GetOkAsync(this HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/v1/triages/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TriageResponse>(JsonSerializerOptions.Web, Ct))!;
    }

    public static Task<HttpResponseMessage> ReviewAsync(this HttpClient client, Guid id, string? decision, string? priority = null) =>
        client.PostAsJsonAsync($"/v1/triages/{id}/review", new ReviewTriageRequest(decision, priority), Ct);

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
