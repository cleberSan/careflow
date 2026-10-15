namespace CareFlow.Contracts;

/// <summary>
/// Evento publicado entre serviços. O tipo vira a routing key no RabbitMQ e não pode mudar depois de publicado:
/// para mudar o formato, crie um tipo novo (por exemplo, <c>consent.revoked.v2</c>) e mantenha o antigo até
/// os consumidores migrarem. Eventos carregam só pseudônimos, nunca dados pessoais.
/// </summary>
public interface IIntegrationEvent
{
    static abstract string EventType { get; }
}
