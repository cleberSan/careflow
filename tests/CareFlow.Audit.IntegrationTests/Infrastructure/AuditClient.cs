using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Audit.Api.Features.Entries;
using CareFlow.Audit.Api.Features.Verification;
using CareFlow.Audit.Domain.Chain;

namespace CareFlow.Audit.IntegrationTests.Infrastructure;

internal static class AuditClient
{
    public const string Read = "audit.read";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static AuditEvent NewEvent(Guid tenantId, Guid? resourceId = null, string action = "patient.read", Guid? messageId = null) =>
        AuditEvent.Create(
            tenantId,
            messageId ?? Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "patient-vault",
            "usuario-sintetico-1",
            action,
            "patient",
            resourceId ?? Guid.NewGuid()).Value;

    public static async Task<ChainVerificationResponse> VerifyOkAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/v1/audit/verification", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChainVerificationResponse>(Json, Ct))!;
    }

    public static async Task<AuditEntryPage> ListOkAsync(this HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/v1/audit/entries{query}", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuditEntryPage>(Json, Ct))!;
    }

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
