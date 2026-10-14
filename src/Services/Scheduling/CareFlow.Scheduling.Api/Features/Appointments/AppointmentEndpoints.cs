using CareFlow.Scheduling.Api.Common.Security;

namespace CareFlow.Scheduling.Api.Features.Appointments;

internal static class AppointmentEndpoints
{
    public static IServiceCollection AddAppointmentFeatures(this IServiceCollection services) =>
        services
            .AddScoped<BookAppointmentHandler>()
            .AddScoped<CancelAppointmentHandler>();

    public static IEndpointRouteBuilder MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var appointments = app.MapGroup("/v1/appointments").WithTags("Appointments");

        appointments.MapPost("/", BookAppointmentEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("BookAppointment")
            .WithSummary("Reserva uma consulta. Exige o cabeçalho Idempotency-Key.");

        appointments.MapGet("/{id:guid}", GetAppointmentEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithName("GetAppointment");

        appointments.MapPost("/{id:guid}/cancel", CancelAppointmentEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("CancelAppointment");

        app.MapGet("/v1/professionals/{professionalId:guid}/appointments", ListProfessionalAppointmentsEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithTags("Appointments")
            .WithName("ListProfessionalAppointments")
            .WithSummary("Lista os horários ocupados de um profissional no período [from, to).");

        return app;
    }
}
