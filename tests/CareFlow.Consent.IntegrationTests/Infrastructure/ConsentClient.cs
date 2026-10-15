using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Consent.Api.Features.Consents;
using CareFlow.Testing;

using Npgsql;

namespace CareFlow.Consent.IntegrationTests.Infrastructure;

internal static class ConsentClient
{
    public const string Read = "consent.read";
    public const string Write = "consent.write";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static GrantConsentRequest Specific(string term = "2026-10") => new("specific-consent", term);

    public static Task<HttpResponseMessage> GrantAsync(this HttpClient client, Guid patientRef, string purpose, GrantConsentRequest request) =>
        client.PutAsJsonAsync($"/v1/patients/{patientRef}/consents/{purpose}", request, Json, Ct);

    public static Task<HttpResponseMessage> RevokeAsync(this HttpClient client, Guid patientRef, string purpose) =>
        client.PostAsync($"/v1/patients/{patientRef}/consents/{purpose}/revoke", content: null, Ct);

    public static async Task<ConsentResponse> ReadConsentAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ConsentResponse>(Json, Ct))!;

    public static async Task<ConsentResponse[]> ListOkAsync(this HttpClient client, Guid patientRef)
    {
        var response = await client.GetAsync($"/v1/patients/{patientRef}/consents", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConsentResponse[]>(Json, Ct))!;
    }

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Eventos que a API gravou no Outbox para o paciente, em ordem (lidos como admin, fora do RLS).</summary>
    public static async Task<List<(string Type, JsonElement Payload)>> OutboxAsync(string adminConnectionString, Guid patientRef)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT type, payload::text FROM outbox_messages
            WHERE payload->>'patientRef' = @p OR payload->>'resourceId' = @p
            ORDER BY occurred_at, id
            """,
            admin);
        command.Parameters.AddWithValue("p", patientRef.ToString());
        var events = new List<(string, JsonElement)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            events.Add((reader.GetString(0), document.RootElement.Clone()));
        }

        return events;
    }
}
