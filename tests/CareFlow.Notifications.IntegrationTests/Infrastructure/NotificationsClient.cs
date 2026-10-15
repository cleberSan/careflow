using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.Contracts.Appointments;
using CareFlow.Notifications.Api.Contacts;
using CareFlow.Notifications.Api.Features.Notifications;
using CareFlow.Testing;

namespace CareFlow.Notifications.IntegrationTests.Infrastructure;

internal static class NotificationsClient
{
    public const string Read = "notifications.read";
    public const string Admin = "notifications.admin";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Consulta daqui a alguns dias, alinhada ao minuto, com fim depois do início.</summary>
    public static AppointmentBooked Booked(Guid patientRef, DateTimeOffset? startsAt = null, Guid? appointmentId = null)
    {
        var start = startsAt ?? DateTimeOffset.UtcNow.Date.AddDays(10).AddHours(13);
        return new AppointmentBooked(appointmentId ?? Guid.NewGuid(), patientRef, start, start.AddMinutes(30), DateTimeOffset.UtcNow);
    }

    public static AppointmentCancelled Cancelled(AppointmentBooked booked, string reason = AppointmentCancellationReasons.Requested) =>
        new(booked.AppointmentId, booked.PatientRef, booked.StartsAt, reason, DateTimeOffset.UtcNow);

    /// <summary>Lista de canais para o corpo das requisições.</summary>
    public static string[] Channels(params string[] names) => names;

    public static PatientContact SyntheticContact(bool phone = true, bool email = true) =>
        new(phone ? SyntheticPatients.Phone() : null, email ? SyntheticPatients.Email() : null);

    public static ContactLookup Found(PatientContact contact) => ContactLookup.Found(contact);

    public static async Task<IReadOnlyList<NotificationResponse>> NotificationsAsync(this HttpClient client, Guid appointmentId)
    {
        var response = await client.GetAsync($"/v1/appointments/{appointmentId}/notifications", Ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<NotificationResponse>>(Json, Ct))!;
    }

    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
