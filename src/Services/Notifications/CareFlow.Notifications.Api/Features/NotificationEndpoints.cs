using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Common.Security;
using CareFlow.Notifications.Api.Features.FaultInjection;
using CareFlow.Notifications.Api.Features.Notifications;
using CareFlow.Notifications.Api.Features.Settings;

namespace CareFlow.Notifications.Api.Features;

internal static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/appointments/{appointmentId:guid}/notifications", ListAppointmentNotificationsEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithTags("Notifications")
            .WithName("ListAppointmentNotifications")
            .WithSummary("Avisos de uma consulta, com status e tentativas por canal.");

        var settings = app.MapGroup("/v1/settings").WithTags("Settings");
        settings.MapGet("/", ClinicSettingsEndpoints.GetAsync)
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithName("GetClinicSettings")
            .WithSummary("Configuração de avisos da clínica (ou os padrões).");
        settings.MapPut("/", ClinicSettingsEndpoints.PutAsync)
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithName("PutClinicSettings")
            .WithSummary("Grava ordem dos canais, nome nas mensagens, fuso e antecedência do lembrete.");

        // Só no ambiente de demonstração: fora dele, as rotas nem existem.
        if (app.ServiceProvider.GetRequiredService<ClinicIsolation>().OverridesEnabled)
        {
            app.MapFaultInjectionEndpoints(AuthorizationPolicies.Admin);
        }

        return app;
    }
}
