using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace CareFlow.BuildingBlocks.AspNetCore.Security;

public static class AuditActor
{
    public const int MaxLength = 128;
    public const string Unknown = "desconhecido";

    /// <summary>
    /// Quem fez a requisição, para a trilha de auditoria: a claim <c>sub</c> do token. Um <c>sub</c> fora do
    /// formato aceito pela auditoria (não ASCII, espaço, longo demais) vira um hash estável, para o evento não
    /// ser recusado e o acesso não deixar de ser registrado.
    /// </summary>
    public static string From(ClaimsPrincipal? user)
    {
        var subject = user?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return Unknown;
        }

        if (subject.Length <= MaxLength && subject.All(c => c is >= '!' and <= '~'))
        {
            return subject;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(subject));
        return "sub-sha256:" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
