using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Domain.Notifications;

/// <summary>
/// Um aviso ao paciente sobre uma consulta. Guarda só pseudônimo, horário e o resultado do envio: o contato é
/// lido do cofre na hora e nunca gravado aqui. Há no máximo um aviso de cada tipo por consulta.
/// </summary>
public sealed class Notification
{
    private Notification()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid AppointmentId { get; private set; }

    public Guid PatientRef { get; private set; }

    public NotificationKind Kind { get; private set; }

    public NotificationStatus Status { get; private set; }

    public DateTimeOffset AppointmentStartsAt { get; private set; }

    /// <summary>Só nos avisos de cancelamento.</summary>
    public CancellationCause? CancellationCause { get; private set; }

    /// <summary>Quando o aviso deve sair (agora, ou a antecedência do lembrete).</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>Rodadas de envio já feitas (cada rodada tenta os canais em ordem).</summary>
    public int Rounds { get; private set; }

    public Channel? SentVia { get; private set; }

    public string? ProviderMessageId { get; private set; }

    public NotSentReason? NotSentReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Controle de concorrência otimista (<c>xmin</c> do PostgreSQL).</summary>
    public uint Version { get; private set; }

    public bool IsPending => Status == NotificationStatus.Pending;

    public static Notification Confirmation(Guid tenantId, Guid appointmentId, Guid patientRef, DateTimeOffset startsAt, DateTimeOffset now) =>
        Create(tenantId, appointmentId, patientRef, NotificationKind.Confirmation, startsAt, now, cause: null, dueAt: now);

    /// <summary>Lembrete na antecedência da clínica, ou <c>null</c> se essa hora já passou (a confirmação basta).</summary>
    public static Notification? Reminder(Guid tenantId, Guid appointmentId, Guid patientRef, DateTimeOffset startsAt, TimeSpan lead, DateTimeOffset now)
    {
        var dueAt = startsAt - lead;
        return dueAt <= now
            ? null
            : Create(tenantId, appointmentId, patientRef, NotificationKind.Reminder, startsAt, now, cause: null, dueAt);
    }

    public static Notification Cancellation(
        Guid tenantId, Guid appointmentId, Guid patientRef, DateTimeOffset startsAt, CancellationCause cause, DateTimeOffset now) =>
        Create(tenantId, appointmentId, patientRef, NotificationKind.Cancellation, startsAt, now, cause, dueAt: now);

    /// <summary>
    /// Chave de idempotência do envio por um canal. É a mesma em todas as retentativas e rodadas, então o
    /// provedor reconhece a repetição e o paciente nunca recebe a mesma mensagem duas vezes pelo mesmo canal.
    /// </summary>
    public string IdempotencyKeyFor(Channel channel) => $"{Id:N}-{channel.Slug()}";

    /// <summary>A consulta foi cancelada antes de a confirmação ou o lembrete sair.</summary>
    public bool Supersede(DateTimeOffset now) => Close(NotificationStatus.Superseded, now);

    public bool Expire(DateTimeOffset now) => Close(NotificationStatus.Expired, now);

    public bool MarkNotSent(NotSentReason reason, DateTimeOffset now)
    {
        if (!Close(NotificationStatus.NotSent, now))
        {
            return false;
        }

        NotSentReason = reason;
        return true;
    }

    /// <summary>
    /// O provedor aceitou. Vale também sobre um aviso já marcado como substituído: se o cancelamento chegou
    /// enquanto a mensagem saía, o envio aconteceu e a trilha precisa dizer isso.
    /// </summary>
    public bool MarkSent(Channel channel, string providerMessageId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerMessageId);
        if (Status is not (NotificationStatus.Pending or NotificationStatus.Superseded))
        {
            return false;
        }

        Status = NotificationStatus.Sent;
        SentVia = channel;
        ProviderMessageId = providerMessageId;
        CompletedAt = now;
        return true;
    }

    /// <summary>Uma rodada inteira falhou. Devolve o total de rodadas para decidir entre tentar de novo ou desistir.</summary>
    public int RegisterFailedRound()
    {
        if (!IsPending)
        {
            throw new InvalidOperationException("Só avisos pendentes recebem novas rodadas.");
        }

        return ++Rounds;
    }

    public bool MarkFailed(DateTimeOffset now) => Close(NotificationStatus.Failed, now);

    private bool Close(NotificationStatus status, DateTimeOffset now)
    {
        if (!IsPending)
        {
            return false;
        }

        Status = status;
        CompletedAt = now;
        return true;
    }

    private static Notification Create(
        Guid tenantId,
        Guid appointmentId,
        Guid patientRef,
        NotificationKind kind,
        DateTimeOffset startsAt,
        DateTimeOffset now,
        CancellationCause? cause,
        DateTimeOffset dueAt)
    {
        if (tenantId == Guid.Empty || appointmentId == Guid.Empty || patientRef == Guid.Empty)
        {
            throw new ArgumentException("Clínica, consulta e paciente são obrigatórios.");
        }

        return new Notification
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            PatientRef = patientRef,
            Kind = kind,
            Status = NotificationStatus.Pending,
            AppointmentStartsAt = startsAt.ToUniversalTime(),
            CancellationCause = cause,
            DueAt = dueAt,
            CreatedAt = now,
        };
    }
}
