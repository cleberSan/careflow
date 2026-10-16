using System.Security.Cryptography;

using CareFlow.BuildingBlocks.Results;

namespace CareFlow.Rights.Domain.Requests;

/// <summary>
/// Pedido do titular, do recebimento ao fim (ADR-0012). A identidade é confirmada por um código de uso único
/// mandado ao contato do cadastro antes de qualquer serviço ser acionado; o prazo de resposta (LGPD art. 19, II)
/// conta do recebimento. Só o resumo do código é guardado, nunca o código.
/// </summary>
public sealed class SubjectRequest
{
    /// <summary>Prazo de referência para a resposta completa (LGPD art. 19, II).</summary>
    public static readonly TimeSpan ResponseDeadline = TimeSpan.FromDays(15);

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public const int MaxCodeAttempts = 5;

    private SubjectRequest()
    {
        // Usado pelo EF Core.
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PatientRef { get; private set; }

    public SubjectRequestKind Kind { get; private set; }

    public SubjectRequestStatus Status { get; private set; }

    public DenialReason? DenialReason { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    /// <summary>HMAC do código (a chave fica fora do banco). Apagado ao confirmar ou negar.</summary>
    public byte[]? CodeHash { get; private set; }

    public DateTimeOffset CodeExpiresAt { get; private set; }

    public int CodeAttempts { get; private set; }

    public DateTimeOffset? CodeSentAt { get; private set; }

    public string? CodeChannel { get; private set; }

    public DateTimeOffset? IdentityConfirmedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Versão da política de retenção aplicada (só eliminação).</summary>
    public string? PolicyVersion { get; private set; }

    /// <summary>Chave privada do pedido (P-256), cifrada pela chave do serviço. Destruída quando o pacote fica pronto.</summary>
    public byte[]? WrappedRecipientKey { get; private set; }

    public bool IsExport => Kind is SubjectRequestKind.Access or SubjectRequestKind.Portability;

    public bool IsFinal => Status is SubjectRequestStatus.Completed or SubjectRequestStatus.Denied;

    /// <summary>Recebe o pedido. <paramref name="codeHash"/> calcula o resumo do código a partir do identificador do pedido.</summary>
    public static Result<SubjectRequest> Receive(
        Guid tenantId, Guid patientRef, SubjectRequestKind kind, Func<Guid, byte[]> codeHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(codeHash);
        if (tenantId == Guid.Empty || patientRef == Guid.Empty)
        {
            return SubjectRequestErrors.MissingIdentifier;
        }

        if (!Enum.IsDefined(kind))
        {
            return SubjectRequestErrors.InvalidKind;
        }

        var receivedAt = ToStoragePrecision(now);
        var id = Guid.CreateVersion7(now);
        return new SubjectRequest
        {
            Id = id,
            TenantId = tenantId,
            PatientRef = patientRef,
            Kind = kind,
            Status = SubjectRequestStatus.Received,
            ReceivedAt = receivedAt,
            DueAt = receivedAt + ResponseDeadline,
            CodeHash = codeHash(id),
            CodeExpiresAt = receivedAt + CodeLifetime,
        };
    }

    public bool IsOverdue(DateTimeOffset now) => !IsFinal && now > DueAt;

    /// <summary>
    /// Resultado do envio do código. Sem contato ou sem entrega, o pedido é negado. Repetições e respostas que
    /// chegam depois de o pedido andar não mudam nada.
    /// </summary>
    public void CodeDispatched(bool sent, bool noContact, string? channel, DateTimeOffset now)
    {
        if (Status != SubjectRequestStatus.Received)
        {
            return;
        }

        if (sent)
        {
            CodeSentAt ??= ToStoragePrecision(now);
            CodeChannel ??= channel;
            return;
        }

        Deny(noContact ? Requests.DenialReason.NoContact : Requests.DenialReason.CodeNotDelivered, now);
    }

    /// <summary>
    /// Confere o código. Compara resumos em tempo constante; o código expirado ou a quinta tentativa errada negam o
    /// pedido, e um pedido novo gera outro código.
    /// </summary>
    public Result<IdentityCheck> ConfirmIdentity(ReadOnlySpan<byte> submittedCodeHash, DateTimeOffset now)
    {
        if (Status != SubjectRequestStatus.Received || CodeHash is null)
        {
            return SubjectRequestErrors.NotAwaitingIdentity;
        }

        if (now >= CodeExpiresAt)
        {
            Deny(Requests.DenialReason.IdentityNotConfirmed, now);
            return IdentityCheck.Expired;
        }

        if (!CryptographicOperations.FixedTimeEquals(CodeHash, submittedCodeHash))
        {
            CodeAttempts++;
            if (CodeAttempts >= MaxCodeAttempts)
            {
                Deny(Requests.DenialReason.IdentityNotConfirmed, now);
                return IdentityCheck.AttemptsExhausted;
            }

            return IdentityCheck.WrongCode;
        }

        Status = SubjectRequestStatus.InProgress;
        IdentityConfirmedAt = ToStoragePrecision(now);
        CodeHash = null;
        return IdentityCheck.Confirmed;
    }

    public int RemainingAttempts => Math.Max(0, MaxCodeAttempts - CodeAttempts);

    /// <summary>Guarda a chave privada do pedido (cifrada) para abrir as seções quando todos responderem.</summary>
    public void AttachRecipientKey(byte[] wrappedPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(wrappedPrivateKey);
        if (!IsExport || Status != SubjectRequestStatus.InProgress)
        {
            throw new InvalidOperationException("Só um pedido de exportação em andamento recebe chave.");
        }

        WrappedRecipientKey = wrappedPrivateKey;
    }

    /// <summary>A eliminação vai para os serviços com esta versão da política.</summary>
    public void ApplyPolicy(string policyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        PolicyVersion = policyVersion;
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != SubjectRequestStatus.InProgress)
        {
            throw new InvalidOperationException("Só um pedido em andamento pode ser concluído.");
        }

        Status = SubjectRequestStatus.Completed;
        CompletedAt = ToStoragePrecision(now);
        DestroyRecipientKey();
    }

    /// <summary>Nega o pedido. Num pedido já final, não muda nada.</summary>
    public void Deny(DenialReason reason, DateTimeOffset now)
    {
        if (IsFinal)
        {
            return;
        }

        Status = SubjectRequestStatus.Denied;
        DenialReason = reason;
        CompletedAt = ToStoragePrecision(now);
        CodeHash = null;
        DestroyRecipientKey();
    }

    private void DestroyRecipientKey()
    {
        if (WrappedRecipientKey is not null)
        {
            CryptographicOperations.ZeroMemory(WrappedRecipientKey);
            WrappedRecipientKey = null;
        }
    }

    private static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
