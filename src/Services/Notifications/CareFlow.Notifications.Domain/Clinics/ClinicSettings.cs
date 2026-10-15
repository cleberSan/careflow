using CareFlow.BuildingBlocks.Results;
using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Domain.Clinics;

/// <summary>
/// Como uma clínica avisa os pacientes: ordem dos canais (fallback), nome que aparece nas mensagens, fuso
/// horário para escrever data e hora e antecedência do lembrete. Clínica sem registro usa os padrões.
/// </summary>
public sealed class ClinicSettings
{
    public const int MinDisplayNameLength = 2;
    public const int MaxDisplayNameLength = 60;
    public const string DefaultTimeZone = "America/Sao_Paulo";

    public static readonly TimeSpan MinReminderLead = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxReminderLead = TimeSpan.FromHours(72);
    public static readonly TimeSpan DefaultReminderLead = TimeSpan.FromHours(24);

    private ClinicSettings()
    {
        // Usado pelo EF Core.
    }

    public Guid TenantId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string TimeZoneId { get; private set; } = DefaultTimeZone;

    public ChannelOrder ChannelOrder { get; private set; } = ChannelOrder.Default;

    public TimeSpan ReminderLead { get; private set; } = DefaultReminderLead;

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Configuração usada enquanto a clínica não grava a sua.</summary>
    public static ClinicSettings Defaults(Guid tenantId, string displayName) =>
        new()
        {
            TenantId = tenantId,
            DisplayName = displayName,
            TimeZoneId = DefaultTimeZone,
            ChannelOrder = ChannelOrder.Default,
            ReminderLead = DefaultReminderLead,
        };

    public static Result<ClinicSettings> Create(
        Guid tenantId,
        string? displayName,
        string? timeZoneId,
        ChannelOrder channelOrder,
        TimeSpan reminderLead,
        DateTimeOffset now)
    {
        var settings = new ClinicSettings { TenantId = tenantId };
        var update = settings.Update(displayName, timeZoneId, channelOrder, reminderLead, now);
        return update.IsSuccess ? settings : update.Error;
    }

    public Result Update(string? displayName, string? timeZoneId, ChannelOrder channelOrder, TimeSpan reminderLead, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channelOrder);

        var name = NormalizeDisplayName(displayName);
        if (name is null)
        {
            return Result.Failure(ClinicErrors.InvalidDisplayName);
        }

        if (string.IsNullOrWhiteSpace(timeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            return Result.Failure(ClinicErrors.InvalidTimeZone);
        }

        if (reminderLead < MinReminderLead || reminderLead > MaxReminderLead || reminderLead.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            return Result.Failure(ClinicErrors.InvalidReminderLead);
        }

        DisplayName = name;
        TimeZoneId = timeZoneId;
        ChannelOrder = channelOrder;
        ReminderLead = reminderLead;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// O nome vai no texto de SMS e WhatsApp: só letras, números, espaço e pouca pontuação. Sem barra, dois-pontos
    /// ou arroba, então ninguém com acesso à configuração transforma a mensagem num link de phishing.
    /// </summary>
    internal static string? NormalizeDisplayName(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (trimmed.Length is < MinDisplayNameLength or > MaxDisplayNameLength)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '&'))
            {
                return null;
            }
        }

        // "www.x.com" passaria pelos caracteres acima; um ponto entre letras sem espaço é recusado.
        for (var i = 1; i < trimmed.Length - 1; i++)
        {
            if (trimmed[i] == '.' && char.IsLetterOrDigit(trimmed[i - 1]) && char.IsLetterOrDigit(trimmed[i + 1]))
            {
                return null;
            }
        }

        return trimmed;
    }
}
