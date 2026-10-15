using CareFlow.Notifications.Api.Infrastructure;

namespace CareFlow.Notifications.Api.Delivery;

internal static class DeliveryServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<JobQueue>();
        services.AddScoped<DeliveryProcessor>();
        services.AddSingleton<NotificationDispatcher>();

        var section = NotificationOptions.SectionName + ":" + nameof(NotificationOptions.DispatcherEnabled);
        if (configuration.GetValue(section, true))
        {
            services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
        }

        return services;
    }
}
