using CareFlow.Notifications.Domain.Channels;

namespace CareFlow.Notifications.Api.Contacts;

/// <summary>
/// Telefone e e-mail do paciente, lidos para um envio e descartados em seguida: nunca gravados nem logados.
/// <see cref="ToString"/> é redigido.
/// </summary>
internal sealed record PatientContact(string? Phone, string? Email)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Phone) && string.IsNullOrWhiteSpace(Email);

    public string? AddressFor(Channel channel)
    {
        var address = channel.UsesPhone() ? Phone : Email;
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    public override string ToString() => "PatientContact { *** }";
}

internal enum ContactLookupStatus
{
    Found = 1,

    /// <summary>O cofre não conhece o paciente nesta clínica.</summary>
    NotFound,

    /// <summary>O cofre não respondeu; tentar de novo mais tarde.</summary>
    Unavailable,
}

internal sealed record ContactLookup(ContactLookupStatus Status, PatientContact? Contact = null)
{
    public static ContactLookup NotFound { get; } = new(ContactLookupStatus.NotFound);

    public static ContactLookup Unavailable { get; } = new(ContactLookupStatus.Unavailable);

    public static ContactLookup Found(PatientContact contact) => new(ContactLookupStatus.Found, contact);
}

/// <summary>Onde o Notifications busca o contato do paciente da clínica atual (ADR-0009).</summary>
internal interface IContactDirectory
{
    Task<ContactLookup> FindAsync(Guid tenantId, Guid patientRef, CancellationToken cancellationToken);
}
