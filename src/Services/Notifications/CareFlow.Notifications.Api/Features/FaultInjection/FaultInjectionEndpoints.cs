using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Results;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CareFlow.Notifications.Api.Features.FaultInjection;

public sealed record ChannelFaultRequest(string? Mode, int DurationSeconds);

public sealed record ChannelFaultView(string Channel, string? Fault, DateTimeOffset? ExpiresAt, string Circuit);

public sealed record ClinicFaultsResponse(string Partition, IReadOnlyList<ChannelFaultView> Channels);

/// <summary>
/// Falhas injetadas por clínica, para o Painel do demo (ADR-0010). Só existem quando
/// <c>FaultInjection:ClinicOverridesEnabled</c> está ligado (recusado em produção) e só valem para as clínicas
/// isoladas: uma clínica comum recebe 403 e nunca mexe no circuito compartilhado. A clínica vem do token (RLS).
/// </summary>
internal static partial class FaultInjectionEndpoints
{
    public const int MinDurationSeconds = 10;
    public const int MaxDurationSeconds = 900;

    private const string LogCategory = "CareFlow.Notifications.Api.Features.FaultInjection";

    public static readonly Error NotIsolated = Error.Forbidden(
        "Notificacoes.ClinicaSemIsolamento",
        "Falhas só podem ser injetadas em clínicas isoladas do ambiente de demonstração.");

    public static readonly Error InvalidChannel = Error.Validation("Notificacoes.CanalInvalido", "Canal desconhecido. Use whatsapp, sms ou email.");

    public static readonly Error InvalidFault = Error.Validation(
        "Notificacoes.FalhaInvalida",
        $"Use o modo Unavailable, Error, Latency ou LostResponse, por {MinDurationSeconds} a {MaxDurationSeconds} segundos.");

    public static IEndpointRouteBuilder MapFaultInjectionEndpoints(this IEndpointRouteBuilder app, string policy)
    {
        var faults = app.MapGroup("/v1/fault-injection").WithTags("FaultInjection").RequireAuthorization(policy);
        faults.MapGet("/", GetAsync).WithName("GetClinicFaults").WithSummary("Falhas e estado do circuito de cada canal da clínica.");
        faults.MapPut("/{channel}", PutAsync).WithName("PutClinicFault").WithSummary("Liga uma falha num canal da clínica, com prazo.");
        faults.MapDelete("/{channel}", DeleteAsync).WithName("DeleteClinicFault").WithSummary("Desliga a falha do canal da clínica.");
        return app;
    }

    public static async Task<Results<Ok<ClinicFaultsResponse>, ProblemHttpResult>> GetAsync(
        NotificationsDbContext db,
        ITenantContext tenant,
        ClinicIsolation isolation,
        ChannelCircuits circuits,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!isolation.IsIsolated(tenant.TenantId))
        {
            return NotIsolated.ToProblem();
        }

        var now = time.GetUtcNow();
        var active = await db.ChannelFaults.AsNoTracking().Where(f => f.ExpiresAt > now)
            .ToDictionaryAsync(f => f.Channel, cancellationToken).ConfigureAwait(false);
        var partition = isolation.PartitionFor(tenant.TenantId);
        var channels = Enum.GetValues<Channel>()
            .Select(channel => new ChannelFaultView(
                channel.Slug(),
                active.TryGetValue(channel, out var fault) ? fault.Mode.ToString() : null,
                active.TryGetValue(channel, out var expiring) ? expiring.ExpiresAt : null,
                circuits.StateOf(channel, partition).ToString()))
            .ToArray();
        return TypedResults.Ok(new ClinicFaultsResponse(partition, channels));
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> PutAsync(
        string channel,
        ChannelFaultRequest request,
        NotificationsDbContext db,
        ITenantContext tenant,
        ClinicIsolation isolation,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!isolation.IsIsolated(tenant.TenantId))
        {
            return NotIsolated.ToProblem();
        }

        if (ParseChannel(channel) is not { } target)
        {
            return InvalidChannel.ToProblem();
        }

        // Só os nomes exatos e só modos que falham: "None" se faz com DELETE.
        if (request.Mode is null
            || !Enum.TryParse<FaultMode>(request.Mode, ignoreCase: false, out var mode)
            || mode == FaultMode.None
            || !Enum.IsDefined(mode)
            || request.DurationSeconds is < MinDurationSeconds or > MaxDurationSeconds)
        {
            return InvalidFault.ToProblem();
        }

        var now = time.GetUtcNow();
        var stored = await db.ChannelFaults.FirstOrDefaultAsync(f => f.Channel == target, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            stored = new ClinicChannelFault { TenantId = tenant.TenantId, Channel = target };
            db.ChannelFaults.Add(stored);
        }

        stored.Mode = mode;
        stored.ExpiresAt = now.AddSeconds(request.DurationSeconds);
        stored.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Duas ligações simultâneas do mesmo canal: a outra venceu, e o efeito pedido é o mesmo.
            db.ChangeTracker.Clear();
        }

        var logger = loggers.CreateLogger(LogCategory);
        LogFaultSet(logger, target, mode, request.DurationSeconds);
        return TypedResults.NoContent();
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string channel,
        NotificationsDbContext db,
        ITenantContext tenant,
        ClinicIsolation isolation,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!isolation.IsIsolated(tenant.TenantId))
        {
            return NotIsolated.ToProblem();
        }

        if (ParseChannel(channel) is not { } target)
        {
            return InvalidChannel.ToProblem();
        }

        await db.ChannelFaults.Where(f => f.Channel == target).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var logger = loggers.CreateLogger(LogCategory);
        LogFaultCleared(logger, target);
        return TypedResults.NoContent();
    }

    private static Channel? ParseChannel(string slug) =>
        Enum.GetValues<Channel>().Cast<Channel?>().FirstOrDefault(c => string.Equals(c!.Value.Slug(), slug, StringComparison.Ordinal));

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Falha {Mode} injetada no canal {Channel} da clínica por {Seconds}s")]
    private static partial void LogFaultSet(ILogger logger, Channel channel, FaultMode mode, int seconds);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Falha removida do canal {Channel} da clínica")]
    private static partial void LogFaultCleared(ILogger logger, Channel channel);
}
