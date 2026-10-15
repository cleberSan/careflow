using System.Net.Http.Json;

using CareFlow.Scheduling.Api.Features.Appointments;

namespace CareFlow.Scheduling.IntegrationTests.Infrastructure;

/// <summary>
/// Antes de cada reserva, deixa o paciente do corpo cadastrado e com consentimento na projeção da agenda.
/// Mantém os testes de reserva focados no que testam; a elegibilidade tem testes próprios sem este atalho.
/// </summary>
internal sealed class EligiblePatientsHandler(SchedulingApiFixture fixture, Guid? tenantId) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (tenantId is not null
            && request.Method == HttpMethod.Post
            && request.RequestUri?.AbsolutePath.TrimEnd('/') == "/v1/appointments"
            && request.Content is not null)
        {
            var booking = await TryReadAsync(request.Content, cancellationToken);
            if (booking is not null && booking.PatientRef != Guid.Empty)
            {
                await fixture.SeedPatientAsync(tenantId.Value, booking.PatientRef, overwrite: false);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    // Corpos inválidos seguem intactos para a API, que é quem precisa recusá-los.
    private static async Task<BookAppointmentRequest?> TryReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync<BookAppointmentRequest>(SchedulingClient.Json, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
