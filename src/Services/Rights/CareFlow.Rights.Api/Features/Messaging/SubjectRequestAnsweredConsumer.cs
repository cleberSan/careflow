using System.Security.Cryptography;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common;
using CareFlow.Rights.Api.Common.Cryptography;
using CareFlow.Rights.Api.Common.Observability;
using CareFlow.Rights.Api.Features.Exports;
using CareFlow.Rights.Api.Features.Requests;
using CareFlow.Rights.Api.Infrastructure.Persistence;
using CareFlow.Rights.Domain.Requests;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Features.Messaging;

/// <summary>
/// Junta as respostas dos serviços. O pedido só conclui quando todos de <see cref="SubjectRequestServices.All"/>
/// responderam: em exportação, abre as seções com a chave do pedido, monta e cifra o pacote e destrói a chave; em
/// eliminação, o comprovante sai das linhas que cada serviço mandou. O cofre sem o paciente nega o pedido.
/// </summary>
internal sealed partial class SubjectRequestAnsweredConsumer(
    RightsDbContext db,
    RightsKeyRing keys,
    PackageCipher cipher,
    RightsMetrics metrics,
    TimeProvider time,
    ILogger<SubjectRequestAnsweredConsumer> logger)
    : IIntegrationEventConsumer<SubjectRequestAnswered>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(SubjectRequestAnswered @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!SubjectRequestServices.All.Contains(@event.Service, StringComparer.Ordinal))
        {
            throw new PoisonMessageException("Direitos.ServicoDesconhecido");
        }

        // Respostas do mesmo pedido chegam em paralelo: sem a trava, duas transações veriam 5 de 6 e ninguém concluiria.
        await SubjectLock.AcquireAsync(db, @event.RequestId, cancellationToken).ConfigureAwait(false);
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == @event.RequestId, cancellationToken).ConfigureAwait(false);
        if (request is null || request.Status != SubjectRequestStatus.InProgress)
        {
            // Pedido de outra clínica, já negado ou já concluído: a resposta atrasada não muda nada.
            return;
        }

        var answers = await db.Answers.Where(a => a.RequestId == request.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (answers.Any(a => a.Service == @event.Service))
        {
            return;
        }

        var now = time.GetUtcNow();
        if (@event.Outcome == SubjectRequestOutcomes.SubjectNotFound)
        {
            request.Deny(DenialReason.SubjectNotFound, now);
            db.Record(request.TenantId, request.PatientRef, RightsAudit.SystemActor, RightsAudit.Denied, now);
            metrics.Finished(RequestNames.Of(request.Kind), RequestNames.Of(request.Status), now - request.ReceivedAt);
            return;
        }

        var answer = Validate(request, @event);
        db.Answers.Add(answer);
        answers.Add(answer);
        if (answers.Count < SubjectRequestServices.All.Count)
        {
            return;
        }

        if (request.IsExport)
        {
            AssemblePackage(request, answers, now);
        }

        request.Complete(now);
        db.Record(request.TenantId, request.PatientRef, RightsAudit.SystemActor, RightsAudit.Completed, now);
        var kind = RequestNames.Of(request.Kind);
        metrics.Finished(kind, RequestNames.Of(request.Status), now - request.ReceivedAt);
        LogCompleted(logger, kind, now > request.DueAt);
    }

    private static SubjectRequestAnswer Validate(SubjectRequest request, SubjectRequestAnswered @event)
    {
        if (request.IsExport)
        {
            if (@event.Outcome != SubjectRequestOutcomes.Exported || string.IsNullOrEmpty(@event.SealedSection))
            {
                throw new PoisonMessageException("Direitos.RespostaIncompativel");
            }

            return SubjectRequestAnswer.Create(request.TenantId, request.Id, @event.Service, @event.Outcome, @event.SealedSection, null, @event.AnsweredAt);
        }

        if (@event.Outcome != SubjectRequestOutcomes.Erased || @event.SealedSection is not null)
        {
            throw new PoisonMessageException("Direitos.RespostaIncompativel");
        }

        // O comprovante só aceita itens da política e do próprio serviço: um serviço não fala pelo outro.
        var allowed = RetentionPolicy.For(@event.Service).Select(rule => rule.Item).ToHashSet(StringComparer.Ordinal);
        if (@event.Erasure.Any(line => !allowed.Contains(line.Item) || line.Records < 0))
        {
            throw new PoisonMessageException("Direitos.ComprovanteInvalido");
        }

        return SubjectRequestAnswer.Create(
            request.TenantId, request.Id, @event.Service, @event.Outcome, null, JsonSerializer.Serialize(@event.Erasure, Json), @event.AnsweredAt);
    }

    private void AssemblePackage(SubjectRequest request, List<SubjectRequestAnswer> answers, DateTimeOffset now)
    {
        if (request.WrappedRecipientKey is null)
        {
            throw new PoisonMessageException("Direitos.PedidoSemChave");
        }

        var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var privateKey = keys.Unwrap(request.WrappedRecipientKey, RightsKeyRing.RecipientKeyContext(request.Id));
        byte[]? zip = null;
        try
        {
            using var recipient = ECDiffieHellman.Create();
            recipient.ImportPkcs8PrivateKey(privateKey, out _);
            foreach (var answer in answers)
            {
                try
                {
                    sections[answer.Service] = SealedBox.Open(
                        recipient, answer.SealedSection!, SubjectRequestSealing.SectionContext(request.Id, answer.Service));
                }
                catch (CryptographicException)
                {
                    // Seção que não abre com a chave deste pedido: copiada de outro pedido ou alterada.
                    throw new PoisonMessageException("Direitos.SecaoInvalida");
                }
            }

            zip = PackageBuilder.Build(request, sections, now);
            var (content, wrappedKey) = cipher.Seal(request.Id, zip);
            var package = SubjectRequestPackage.Create(request.TenantId, request.Id, content, wrappedKey, zip.LongLength, now);
            db.Packages.Add(package);
            db.PackageExpirations.Add(new PackageExpiration { RequestId = request.Id, TenantId = request.TenantId, ExpiresAt = package.ExpiresAt });
            foreach (var answer in answers)
            {
                answer.DiscardSection();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            foreach (var section in sections.Values)
            {
                CryptographicOperations.ZeroMemory(section);
            }

            if (zip is not null)
            {
                CryptographicOperations.ZeroMemory(zip);
            }
        }
    }

    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "Pedido do titular concluído ({Kind}, fora do prazo: {Overdue})")]
    private static partial void LogCompleted(ILogger logger, string kind, bool overdue);
}
