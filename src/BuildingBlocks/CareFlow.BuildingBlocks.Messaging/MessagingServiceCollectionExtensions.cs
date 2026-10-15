using CareFlow.BuildingBlocks.Messaging.Broker;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Diagnostics;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CareFlow.BuildingBlocks.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registra a mensageria do serviço. <paramref name="relayConnectionStringName"/> é a connection string do
    /// login do despachante (papel <c>{serviço}_relay</c>), exigida só quando a mensageria está ligada.
    /// </summary>
    public static MessagingBuilder AddCareFlowMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string relayConnectionStringName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(relayConnectionStringName);

        services.AddOptions<MessagingOptions>()
            .BindConfiguration(MessagingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<TenantScope>();
        services.TryAddSingleton<MessagingDiagnostics>();
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<OutboxSignal>();
        services.AddOptions<MessageRelayOptions>();
        services.TryAddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(relayConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Configure a connection string '{relayConnectionStringName}' (variável ConnectionStrings__{relayConnectionStringName}).");
            }

            return new RelayDataSource(connectionString);
        });

        var enabled = configuration.GetValue(MessagingOptions.SectionName + ":" + nameof(MessagingOptions.Enabled), true);
        if (enabled)
        {
            services.AddHostedService<MessageRelay>();
            services.AddHealthChecks()
                .AddCheck<RelayRoleHealthCheck>("messaging-relay-role", tags: ["ready"])
                .AddCheck<BrokerHealthCheck>("messaging-broker", tags: ["messaging"]);
        }

        return new MessagingBuilder(services, enabled);
    }
}

public sealed class MessagingBuilder
{
    private readonly IServiceCollection _services;
    private readonly bool _enabled;

    internal MessagingBuilder(IServiceCollection services, bool enabled)
    {
        _services = services;
        _enabled = enabled;
    }

    /// <summary>Liga o Outbox: o interceptor acorda o despachante e o despachante publica os eventos gravados.</summary>
    public MessagingBuilder AddOutbox()
    {
        _services.AddScoped<IInterceptor, OutboxSaveChangesInterceptor>();
        _services.Configure<MessageRelayOptions>(relay => relay.DispatchOutbox = true);
        return this;
    }

    /// <summary>Liga o consumidor com Inbox no DbContext do serviço e registra os handlers.</summary>
    public MessagingBuilder AddConsumer<TContext>(Action<ConsumerRegistry> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);

        var registry = new ConsumerRegistry(_services);
        configure(registry);
        _services.AddSingleton(registry);
        _services.AddSingleton<InboxProcessor<TContext>>();
        _services.Configure<MessageRelayOptions>(relay => relay.CleanInbox = true);

        if (_enabled)
        {
            _services.AddHostedService<RabbitMqConsumer<TContext>>();
        }

        return this;
    }
}
