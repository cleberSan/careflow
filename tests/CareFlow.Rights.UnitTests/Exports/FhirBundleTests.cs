using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Features.Exports;

using Firely.Fhir.Validation;

using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;

namespace CareFlow.Rights.UnitTests.Exports;

/// <summary>
/// O Bundle de portabilidade validado contra os perfis base do FHIR R4 (estrutura, cardinalidade, invariantes e
/// bindings obrigatórios), com o validador da Firely e a especificação oficial empacotada, sem rede.
/// </summary>
public sealed class FhirBundleTests
{
    private static readonly Lazy<Validator> SharedValidator = new(() =>
    {
        var resolver = new CachedResolver(ZipSource.CreateValidationSource());
        return new Validator(resolver.AsAsync(), LocalTerminologyService.CreateDefaultForCore(resolver.AsAsync()));
    });

    private static readonly Guid Patient = Guid.Parse("0190f0a0-0000-7000-8000-000000000001");

    private static VaultSection Vault(string? email = "sintetico.a1b2@example.com", string? phone = "5511912345678") =>
        new(Patient, "Paciente Sintetico A1B2", "52998224725", new DateOnly(1990, 5, 17), email, phone, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static SchedulingSection Scheduling() => new(
    [
        new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10).AddMinutes(30), "Booked", DateTimeOffset.UtcNow.AddDays(-20), null, null),
        new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(3).AddMinutes(30), "Cancelled", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1), "SubjectErasure"),
    ]);

    private static ConsentSection Consents() => new(
        [
            new("scheduling", "consent", "2026-10", "Granted", DateTimeOffset.UtcNow.AddDays(-30), null, 1),
            new("ai-triage", "specific-consent", "2026-10", "Revoked", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1), 2),
        ],
        []);

    private static IReadOnlyList<OperationOutcome.IssueComponent> Errors(Resource resource) =>
        [.. SharedValidator.Value.Validate(resource).Issue.Where(issue => issue.Severity is OperationOutcome.IssueSeverity.Error or OperationOutcome.IssueSeverity.Fatal)];

    [Fact]
    public void Bundle_IsValidAgainstTheR4BaseProfiles()
    {
        // Arrange
        var bundle = FhirBundleBuilder.Build(Guid.NewGuid(), Vault(), Scheduling(), Consents(), Patient, DateTimeOffset.UtcNow);

        // Act
        var errors = Errors(bundle);

        // Assert
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Expression?.FirstOrDefault()}: {e.Details?.Text}")));
        Assert.Equal(Bundle.BundleType.Collection, bundle.Type);
        Assert.Equal(["Patient", "Appointment", "Appointment", "Consent", "Consent"], bundle.Entry.Select(e => e.Resource!.TypeName).ToArray());
    }

    [Fact]
    public void Bundle_WithoutContactsOrHistory_IsStillValid()
    {
        var bundle = FhirBundleBuilder.Build(Guid.NewGuid(), Vault(email: null, phone: null), new SchedulingSection([]), new ConsentSection([], []), Patient, DateTimeOffset.UtcNow);

        Assert.Empty(Errors(bundle));
        Assert.Single(bundle.Entry);
    }

    [Fact]
    public void Patient_CarriesCpfAsRndsIdentifier_AndEveryReferenceResolvesInsideTheBundle()
    {
        // Act
        var bundle = FhirBundleBuilder.Build(Guid.NewGuid(), Vault(), Scheduling(), Consents(), Patient, DateTimeOffset.UtcNow);

        // Assert
        var patient = Assert.IsType<Patient>(bundle.Entry[0].Resource);
        Assert.Equal(FhirBundleBuilder.CpfSystem, patient.Identifier.Single().System);
        Assert.Equal("1990-05-17", patient.BirthDate);
        Assert.Equal(2, patient.Telecom.Count);

        var fullUrls = bundle.Entry.Select(e => e.FullUrl).ToHashSet();
        var appointments = bundle.Entry.Select(e => e.Resource).OfType<Appointment>().ToList();
        Assert.All(appointments, a => Assert.Contains(a.Participant[0].Actor!.Reference, fullUrls));
        Assert.Equal(Appointment.AppointmentStatus.Cancelled, appointments[1].Status);
        Assert.Equal("SubjectErasure", appointments[1].CancelationReason!.Text);
        var consents = bundle.Entry.Select(e => e.Resource).OfType<Consent>().ToList();
        Assert.All(consents, c => Assert.Contains(c.Patient!.Reference, fullUrls));
        Assert.Equal([Consent.ConsentState.Active, Consent.ConsentState.Inactive], consents.Select(c => c.Status!.Value).ToArray());
    }

    [Fact]
    public void ConsentIds_AreStablePerPatientAndPurpose()
    {
        var first = FhirBundleBuilder.Build(Guid.NewGuid(), Vault(), null, Consents(), Patient, DateTimeOffset.UtcNow);
        var second = FhirBundleBuilder.Build(Guid.NewGuid(), Vault(), null, Consents(), Patient, DateTimeOffset.UtcNow);

        Assert.Equal(first.Entry.Skip(1).Select(e => e.FullUrl), second.Entry.Skip(1).Select(e => e.FullUrl));
        Assert.Equal(2, first.Entry.Skip(1).Select(e => e.FullUrl).Distinct().Count());
    }

    [Fact]
    public void Validator_RejectsAnInvalidResource()
    {
        // Prova de que o validador está de fato conferindo: Appointment sem participante e sem status viola o R4.
        var invalid = new Appointment();

        Assert.NotEmpty(Errors(invalid));
    }

    [Fact]
    public void Serialize_ProducesFhirJson()
    {
        var bundle = FhirBundleBuilder.Build(Guid.NewGuid(), Vault(), null, null, Patient, DateTimeOffset.UtcNow);

        using var json = System.Text.Json.JsonDocument.Parse(FhirBundleBuilder.Serialize(bundle));

        Assert.Equal("Bundle", json.RootElement.GetProperty("resourceType").GetString());
        Assert.Equal("Patient", json.RootElement.GetProperty("entry")[0].GetProperty("resource").GetProperty("resourceType").GetString());
    }
}
