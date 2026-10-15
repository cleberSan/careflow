namespace CareFlow.Triage.Domain.Pseudonymization;

/// <summary>Tipo de identificador removido do texto livre. O marcador entra no texto no lugar do valor.</summary>
public enum MaskKind
{
    Name = 1,
    Cpf,
    Phone,
    Email,
    Link,
    Handle,
    Date,
    Age,
    Address,
    Number,
}

public static class MaskKindExtensions
{
    public static string Placeholder(this MaskKind kind) => kind switch
    {
        MaskKind.Name => "[nome]",
        MaskKind.Cpf => "[cpf]",
        MaskKind.Phone => "[telefone]",
        MaskKind.Email => "[email]",
        MaskKind.Link => "[link]",
        MaskKind.Handle => "[usuario]",
        MaskKind.Date => "[data]",
        MaskKind.Age => "[idade]",
        MaskKind.Address => "[endereco]",
        MaskKind.Number => "[numero]",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Slug(this MaskKind kind) => kind.ToString().ToLowerInvariant();
}
