using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using CareFlow.Demo.Api.Common;

using Microsoft.Extensions.Options;

namespace CareFlow.Demo.Api.Sessions;

/// <summary>Uma visita ao Painel: clínica e paciente sintéticos próprios e uma partição isolada no Notifications.</summary>
internal sealed class DemoSession(string id, Guid clinic, int slot, DateTimeOffset createdAt, DateTimeOffset expiresAt)
{
    private readonly Lock _gate = new();
    private readonly List<Guid> _appointments = [];
    private readonly HashSet<string> _traces = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _triages = [];
    private readonly List<Guid> _rightsRequests = [];
    private int _bookingsReserved;
    private int _triagesReserved;
    private int _rightsReserved;

    /// <summary>Hash SHA-256 do valor do cookie; o valor em si nunca é guardado.</summary>
    public string Id { get; } = id;

    public Guid Clinic { get; } = clinic;

    public int Slot { get; } = slot;

    public Guid PatientRef { get; set; }

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public IReadOnlyList<Guid> Appointments
    {
        get
        {
            lock (_gate)
            {
                return [.. _appointments];
            }
        }
    }

    /// <summary>Reserva uma vaga de consulta da sessão (conta mesmo se a agenda recusar); falso no limite.</summary>
    public bool TryReserveBooking(int limit, out int index)
    {
        lock (_gate)
        {
            index = _bookingsReserved;
            if (_bookingsReserved >= limit)
            {
                return false;
            }

            _bookingsReserved++;
            return true;
        }
    }

    public void AddAppointment(Guid appointment)
    {
        lock (_gate)
        {
            _appointments.Add(appointment);
        }
    }

    /// <summary>Reserva uma triagem da sessão (conta mesmo se a triagem recusar a queixa); falso no limite.</summary>
    public bool TryReserveTriage(int limit)
    {
        lock (_gate)
        {
            if (_triagesReserved >= limit)
            {
                return false;
            }

            _triagesReserved++;
            return true;
        }
    }

    public void AddTriage(Guid triage)
    {
        lock (_gate)
        {
            _triages.Add(triage);
        }
    }

    /// <summary>Só triagens que a própria sessão criou podem ser revisadas por ela (o id vem do navegador).</summary>
    public bool OwnsTriage(Guid triage)
    {
        lock (_gate)
        {
            return _triages.Contains(triage);
        }
    }

    /// <summary>Reserva um pedido do titular da sessão (conta mesmo se o serviço recusar); falso no limite.</summary>
    public bool TryReserveRightsRequest(int limit)
    {
        lock (_gate)
        {
            if (_rightsReserved >= limit)
            {
                return false;
            }

            _rightsReserved++;
            return true;
        }
    }

    public void AddRightsRequest(Guid request)
    {
        lock (_gate)
        {
            _rightsRequests.Add(request);
        }
    }

    /// <summary>Só pedidos que a própria sessão abriu (o id vem do navegador).</summary>
    public bool OwnsRightsRequest(Guid request)
    {
        lock (_gate)
        {
            return _rightsRequests.Contains(request);
        }
    }

    /// <summary>Traces criados por esta sessão: só eles podem ser lidos por ela.</summary>
    public void AddTrace(string traceId)
    {
        lock (_gate)
        {
            _traces.Add(traceId);
        }
    }

    public bool OwnsTrace(string traceId)
    {
        lock (_gate)
        {
            return _traces.Contains(traceId);
        }
    }

    public IReadOnlyList<string> Traces
    {
        get
        {
            lock (_gate)
            {
                return [.. _traces];
            }
        }
    }
}

/// <summary>
/// Sessões e partições do Painel, em memória. O Demo roda com uma instância só (as gravações do CI e o uso
/// local); hospedar o Painel ao vivo com mais réplicas troca isto por um armazenamento compartilhado (ADR-0010).
/// </summary>
/// <remarks>
/// O cookie leva 32 bytes aleatórios; aqui fica só o hash, então um despejo de memória não dá cookies válidos.
/// Uma partição só volta ao pool depois que a limpeza da clínica anterior terminou e o resfriamento passou.
/// </remarks>
internal sealed class DemoSessions(IOptions<DemoOptions> options, TimeProvider time)
{
    private readonly DemoOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, DemoSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _slotsGate = new();
    private readonly Dictionary<int, SlotState> _slots = Enumerable.Range(options.Value.FirstSlot, options.Value.Slots).ToDictionary(slot => slot, _ => SlotState.Free(DateTimeOffset.MinValue));

    public int Active => _sessions.Count;

    /// <summary>Pega uma partição livre e fora do resfriamento, ou nada quando o pool está cheio.</summary>
    public int? ReserveSlot()
    {
        var now = time.GetUtcNow();
        lock (_slotsGate)
        {
            foreach (var (slot, state) in _slots.OrderBy(entry => entry.Key))
            {
                if (!state.InUse && state.AvailableAt <= now)
                {
                    _slots[slot] = SlotState.Taken;
                    return slot;
                }
            }
        }

        return null;
    }

    /// <summary>Devolve a partição ao pool, disponível depois do resfriamento.</summary>
    public void ReleaseSlot(int slot, TimeSpan? cooldown = null)
    {
        lock (_slotsGate)
        {
            _slots[slot] = SlotState.Free(time.GetUtcNow() + (cooldown ?? _options.SlotCooldown));
        }
    }

    /// <summary>Cria a sessão e devolve o valor do cookie (o único momento em que ele existe no servidor).</summary>
    public (DemoSession Session, string CookieValue) Open(Guid clinic, int slot)
    {
        var cookie = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        var session = new DemoSession(Hash(cookie), clinic, slot, now, now + _options.SessionLifetime);
        _sessions[session.Id] = session;
        return (session, cookie);
    }

    public DemoSession? Find(string? cookieValue)
    {
        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length > 64)
        {
            return null;
        }

        return _sessions.TryGetValue(Hash(cookieValue), out var session) && session.ExpiresAt > time.GetUtcNow() ? session : null;
    }

    /// <summary>Tira a sessão do mapa (o cookie para de valer na hora); a limpeza vem depois.</summary>
    public bool Close(DemoSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryRemove(session.Id, out _);
    }

    public IReadOnlyList<DemoSession> TakeExpired()
    {
        var now = time.GetUtcNow();
        var expired = new List<DemoSession>();
        foreach (var session in _sessions.Values.Where(session => session.ExpiresAt <= now))
        {
            if (_sessions.TryRemove(session.Id, out _))
            {
                expired.Add(session);
            }
        }

        return expired;
    }

    public IReadOnlyList<DemoSession> TakeAll()
    {
        var all = new List<DemoSession>();
        foreach (var id in _sessions.Keys)
        {
            if (_sessions.TryRemove(id, out var session))
            {
                all.Add(session);
            }
        }

        return all;
    }

    private static string Hash(string cookie) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));

    private readonly record struct SlotState(bool InUse, DateTimeOffset AvailableAt)
    {
        public static SlotState Taken => new(true, DateTimeOffset.MaxValue);

        public static SlotState Free(DateTimeOffset availableAt) => new(false, availableAt);
    }
}
