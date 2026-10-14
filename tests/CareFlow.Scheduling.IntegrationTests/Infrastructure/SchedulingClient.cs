using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.Scheduling.Api.Features.Appointments;

namespace CareFlow.Scheduling.IntegrationTests.Infrastructure;

internal static class SchedulingClient
{
    public const string Read = "scheduling.read";
    public const string Write = "scheduling.write";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Um horário futuro e fixo por teste, alinhado ao minuto.</summary>
    public static DateTimeOffset FutureSlot(int dayOffset = 30, int hour = 9) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(dayOffset).AddHours(hour);

    public static Task<HttpResponseMessage> BookAsync(
        this HttpClient client,
        BookAppointmentRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/appointments")
        {
            Content = JsonContent.Create(request, options: Json),
        };

        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(message, cancellationToken);
    }

    public static BookAppointmentRequest Request(Guid professionalId, DateTimeOffset startsAt, int minutes = 30, Guid? patientRef = null) =>
        new(professionalId, patientRef ?? Guid.NewGuid(), startsAt, startsAt.AddMinutes(minutes));

    public static string NewKey() => Guid.NewGuid().ToString("N");

    public static async Task<AppointmentResponse> ReadAppointmentAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AppointmentResponse>(Json))!;

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
