using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CareFlow.Rights.IntegrationTests.Infrastructure;

internal static class RightsClient
{
    public const string Read = "rights.read";
    public const string Write = "rights.write";
    public const string Admin = "rights.admin";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> OpenAsync(this HttpClient client, Guid patientRef, string kind) =>
        client.PostAsJsonAsync("/v1/subject-requests", new { patientRef, kind }, Ct);

    public static async Task<RequestDto> OpenOkAsync(this HttpClient client, Guid patientRef, string kind)
    {
        using var response = await client.OpenAsync(patientRef, kind);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    public static Task<HttpResponseMessage> GetAsync(this HttpClient client, Guid requestId) =>
        client.GetAsync($"/v1/subject-requests/{requestId}", Ct);

    public static async Task<RequestDto> GetOkAsync(this HttpClient client, Guid requestId)
    {
        using var response = await client.GetAsync(requestId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, Guid requestId, string code) =>
        client.PostAsJsonAsync($"/v1/subject-requests/{requestId}/identity-confirmation", new { code }, Ct);

    public static Task<HttpResponseMessage> CreateLinkAsync(this HttpClient client, Guid requestId) =>
        client.PostAsync($"/v1/subject-requests/{requestId}/download-links", null, Ct);

    public static async Task<LinkDto> CreateLinkOkAsync(this HttpClient client, Guid requestId)
    {
        using var response = await client.CreateLinkAsync(requestId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LinkDto>(Json, Ct))!;
    }

    public static Task<HttpResponseMessage> DownloadAsync(this HttpClient client, string token) =>
        client.PostAsJsonAsync("/v1/downloads", new { token }, Ct);

    public static Task<HttpResponseMessage> ReplayAsync(this HttpClient client, Guid requestId) =>
        client.PostAsync($"/v1/subject-requests/{requestId}/erasure-replay", null, Ct);

    public static async Task<ProblemDto> ProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Json, Ct))!;
}

internal sealed record ServiceDto(string Service, string? Outcome, DateTimeOffset? AnsweredAt);

internal sealed record ReceiptLineDto(string Service, string Item, string Description, string Action, int Records, DateTimeOffset? RetainedUntil, string Basis);

internal sealed record ReceiptDto(string PolicyVersion, DateTimeOffset ErasedAt, IReadOnlyList<ReceiptLineDto> Lines);

internal sealed record PackageDto(bool Available, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DestroyedAt);

internal sealed record RequestDto(
    Guid Id,
    Guid PatientRef,
    string Kind,
    string Status,
    string? DenialReason,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    bool Overdue,
    DateTimeOffset? CodeSentAt,
    string? CodeChannel,
    DateTimeOffset? IdentityConfirmedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ServiceDto> Services,
    ReceiptDto? Receipt,
    PackageDto? Package);

internal sealed record LinkDto(string Token, DateTimeOffset ExpiresAt);

internal sealed record ProblemDto(int Status, string? Code, string? Detail, int? RemainingAttempts)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
