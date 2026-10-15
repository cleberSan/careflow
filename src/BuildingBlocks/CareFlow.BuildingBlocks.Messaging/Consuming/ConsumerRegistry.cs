using CareFlow.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace CareFlow.BuildingBlocks.Messaging.Consuming;

internal delegate Task<bool> EventDispatcher(IServiceProvider services, ReadOnlyMemory<byte> body, MessageContext context, CancellationToken cancellationToken);

/// <summary>Eventos que este serviço consome e o handler de cada um. Montado na subida, sem reflexão por mensagem.</summary>
public sealed class ConsumerRegistry
{
    private readonly Dictionary<string, EventDispatcher> _dispatchers = new(StringComparer.Ordinal);
    private readonly IServiceCollection _services;

    internal ConsumerRegistry(IServiceCollection services)
    {
        _services = services;
    }

    public IReadOnlyCollection<string> EventTypes => _dispatchers.Keys;

    public ConsumerRegistry Handle<TEvent, THandler>()
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventConsumer<TEvent>
    {
        if (!_dispatchers.TryAdd(TEvent.EventType, Dispatch<TEvent>))
        {
            throw new InvalidOperationException($"O evento '{TEvent.EventType}' já tem um handler neste serviço.");
        }

        _services.AddScoped<IIntegrationEventConsumer<TEvent>, THandler>();
        return this;
    }

    internal bool TryGet(string eventType, out EventDispatcher dispatcher) =>
        _dispatchers.TryGetValue(eventType, out dispatcher!);

    private static async Task<bool> Dispatch<TEvent>(
        IServiceProvider services,
        ReadOnlyMemory<byte> body,
        MessageContext context,
        CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
    {
        TEvent? @event;
        try
        {
            @event = EventSerializer.Deserialize<TEvent>(body.Span);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (@event is null)
        {
            return false;
        }

        var handler = services.GetRequiredService<IIntegrationEventConsumer<TEvent>>();
        try
        {
            await handler.HandleAsync(@event, context, cancellationToken).ConfigureAwait(false);
        }
        catch (PoisonMessageException)
        {
            return false;
        }

        return true;
    }
}
