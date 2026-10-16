using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Outbox;
using CareFlow.Contracts.Rights;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.BuildingBlocks.Messaging.Rights;

/// <summary>
/// Respostas de um serviço a um pedido do titular (ADR-0012). Entram no Outbox na mesma transação do Inbox que tratou
/// <see cref="SubjectRequestOpened"/>: ou o serviço aplicou o pedido e respondeu, ou nada aconteceu e a mensagem volta.
/// A seção do pacote vai cifrada para a chave do pedido, porque Outbox e broker guardam a mensagem por dias.
/// </summary>
public static class SubjectRequestAnswers
{
    /// <summary>Formato das seções: o mesmo JSON que vai para o pacote, legível por qualquer ferramenta.</summary>
    public static JsonSerializerOptions SectionJson { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static void AnswerExport<TSection>(
        this DbContext db, Guid tenantId, SubjectRequestOpened request, string service, TSection section, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(request);

        // Sem a chave do pedido não há como entregar sem expor: a mensagem vai para a DLQ em vez de seguir em claro.
        if (!SubjectRequestKinds.IsExport(request.Kind) || string.IsNullOrWhiteSpace(request.RecipientPublicKey))
        {
            throw new InvalidOperationException("Pedido de exportação sem chave pública do pedido.");
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(section, SectionJson);
        try
        {
            var sealedSection = SealedBox.Seal(
                request.RecipientPublicKey, plaintext, SubjectRequestSealing.SectionContext(request.RequestId, service));
            db.Enqueue(tenantId, new SubjectRequestAnswered(request.RequestId, service, SubjectRequestOutcomes.Exported, sealedSection, [], now), now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static void AnswerErasure(
        this DbContext db, Guid tenantId, Guid requestId, string service, IReadOnlyList<ErasureLine> lines, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lines);
        db.Enqueue(tenantId, new SubjectRequestAnswered(requestId, service, SubjectRequestOutcomes.Erased, null, lines, now), now);
    }

    /// <summary>Só o cofre usa: o paciente não existe nesta clínica (ou já foi eliminado), e o pedido é negado.</summary>
    public static void AnswerSubjectNotFound(this DbContext db, Guid tenantId, Guid requestId, string service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.Enqueue(tenantId, new SubjectRequestAnswered(requestId, service, SubjectRequestOutcomes.SubjectNotFound, null, [], now), now);
    }
}
