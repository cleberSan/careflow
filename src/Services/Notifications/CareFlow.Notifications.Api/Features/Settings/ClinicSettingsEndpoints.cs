using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.Persistence;
using CareFlow.BuildingBlocks.Results;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Notifications.Api.Infrastructure;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Channels;
using CareFlow.Notifications.Domain.Clinics;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Features.Settings;

public sealed record ClinicSettingsRequest(string? DisplayName, string? TimeZone, string[]? ChannelOrder, int ReminderLeadMinutes);

public sealed record ClinicSettingsResponse(string DisplayName, string TimeZone, string[] ChannelOrder, int ReminderLeadMinutes, bool IsDefault)
{
    internal static ClinicSettingsResponse From(ClinicSettings settings, bool isDefault) =>
        new(
            settings.DisplayName,
            settings.TimeZoneId,
            [.. settings.ChannelOrder.Channels.Select(c => c.ToString())],
            (int)settings.ReminderLead.TotalMinutes,
            isDefault);
}

internal static partial class ClinicSettingsEndpoints
{
    private const string LogCategory = "CareFlow.Notifications.Api.Features.Settings.ClinicSettings";

    public static readonly Error ConcurrentChange = Error.Conflict(
        "Notificacoes.ConfiguracaoAlteradaAoMesmoTempo",
        "A configuração foi alterada por outra requisição ao mesmo tempo. Leia de novo e repita.");

    public static async Task<Ok<ClinicSettingsResponse>> GetAsync(
        NotificationsDbContext db,
        ITenantContext tenant,
        IOptions<NotificationOptions> options,
        CancellationToken cancellationToken)
    {
        var stored = await db.ClinicSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(stored is null
            ? ClinicSettingsResponse.From(ClinicSettings.Defaults(tenant.TenantId, options.Value.DefaultClinicName), isDefault: true)
            : ClinicSettingsResponse.From(stored, isDefault: false));
    }

    public static async Task<Results<Ok<ClinicSettingsResponse>, ProblemHttpResult>> PutAsync(
        ClinicSettingsRequest request,
        NotificationsDbContext db,
        ITenantContext tenant,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggers.CreateLogger(LogCategory);

        var order = ParseOrder(request.ChannelOrder);
        if (order.IsFailure)
        {
            return order.Error.ToProblem();
        }

        var now = time.GetUtcNow();
        var lead = TimeSpan.FromMinutes(request.ReminderLeadMinutes);
        var stored = await db.ClinicSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            var created = ClinicSettings.Create(tenant.TenantId, request.DisplayName, request.TimeZone, order.Value, lead, now);
            if (created.IsFailure)
            {
                return created.Error.ToProblem();
            }

            stored = created.Value;
            db.ClinicSettings.Add(stored);
        }
        else
        {
            var updated = stored.Update(request.DisplayName, request.TimeZone, order.Value, lead, now);
            if (updated.IsFailure)
            {
                return updated.Error.ToProblem();
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation("pk_clinic_settings"))
        {
            return ConcurrentChange.ToProblem();
        }

        LogUpdated(logger, stored.ChannelOrder);
        return TypedResults.Ok(ClinicSettingsResponse.From(stored, isDefault: false));
    }

    private static Result<ChannelOrder> ParseOrder(string[]? names)
    {
        var channels = new List<Channel>();
        foreach (var name in names ?? [])
        {
            // Só os nomes exatos: "1" ou "whatsapp " não viram canal por acidente.
            if (!Enum.GetNames<Channel>().Contains(name, StringComparer.Ordinal))
            {
                return ClinicErrors.InvalidChannelOrder;
            }

            channels.Add(Enum.Parse<Channel>(name));
        }

        return ChannelOrder.Create(channels);
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Configuração de avisos da clínica atualizada; ordem dos canais {ChannelOrder}")]
    private static partial void LogUpdated(ILogger logger, ChannelOrder channelOrder);
}
