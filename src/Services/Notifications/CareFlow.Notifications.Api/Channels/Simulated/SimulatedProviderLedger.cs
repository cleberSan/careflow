using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Api.Channels.Simulated;

/// <summary>Uma mensagem aceita pelo provedor simulado. Sem o contato: só uma impressão digital dele.</summary>
internal sealed record SimulatedMessage(
    string ProviderMessageId,
    Channel Channel,
    string IdempotencyKey,
    string RecipientFingerprint,
    string Text,
    DateTimeOffset AcceptedAt);

/// <summary>
/// O que os provedores simulados "entregaram", em memória. Faz o papel da deduplicação de um provedor real:
/// a mesma chave de idempotência devolve a mensagem já aceita, sem uma segunda entrega.
/// </summary>
internal sealed class SimulatedProviderLedger : IDisposable
{
    private readonly ConcurrentDictionary<(Channel Channel, string Key), SimulatedMessage> _messages = new();

    // Chave aleatória por processo: a impressão digital não pode ser revertida por força bruta sobre telefones.
    private readonly HMACSHA256 _fingerprint = new(RandomNumberGenerator.GetBytes(32));
    private readonly Lock _fingerprintLock = new();

    public IReadOnlyCollection<SimulatedMessage> Messages => [.. _messages.Values];

    public (SimulatedMessage Message, bool Created) Accept(Channel channel, string idempotencyKey, string recipient, string text, DateTimeOffset now)
    {
        var key = (channel, idempotencyKey);
        if (_messages.TryGetValue(key, out var existing))
        {
            return (existing, false);
        }

        var candidate = new SimulatedMessage(
            $"sim-{channel.Slug()}-{Guid.CreateVersion7(now):N}", channel, idempotencyKey, Fingerprint(recipient), text, now);

        // Duas chamadas simultâneas com a mesma chave: só uma grava, a outra recebe a mensagem gravada.
        return _messages.TryAdd(key, candidate) ? (candidate, true) : (_messages[key], false);
    }

    public SimulatedMessage? Find(Channel channel, string idempotencyKey) =>
        _messages.TryGetValue((channel, idempotencyKey), out var message) ? message : null;

    public string Fingerprint(string recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        lock (_fingerprintLock)
        {
            return Convert.ToHexStringLower(_fingerprint.ComputeHash(Encoding.UTF8.GetBytes(recipient)));
        }
    }

    public void Dispose() => _fingerprint.Dispose();
}
