using System.Globalization;

using CareFlow.BuildingBlocks.Results;
using CareFlow.PatientVault.Domain.Patients;

namespace CareFlow.PatientVault.Api.Common.Http;

/// <summary>ETag forte a partir da versão da linha (<c>xmin</c>), para concorrência otimista com If-Match.</summary>
internal static class EntityTag
{
    public static string Format(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Exige If-Match com um único ETag forte. <c>*</c>, listas e ETags fracos são recusados: aceitar
    /// qualquer versão anularia a proteção contra sobrescrever alterações de outra pessoa.
    /// </summary>
    public static Result<uint> ParseIfMatch(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return PatientErrors.VersionRequired;
        }

        var value = ifMatch.Trim();
        return value.Length > 2
               && value[0] == '"'
               && value[^1] == '"'
               && uint.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : PatientErrors.StaleVersion;
    }
}
