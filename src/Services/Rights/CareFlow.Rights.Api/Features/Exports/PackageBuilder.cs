using System.IO.Compression;
using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Features.Requests;
using CareFlow.Rights.Domain.Requests;

namespace CareFlow.Rights.Api.Features.Exports;

/// <summary>
/// Monta o pacote do titular em memória: <c>leia-me.html</c> (legível), <c>dados.json</c> (estruturado, todas as
/// seções como cada serviço mandou) e <c>fhir-bundle.json</c> (Patient, Appointment e Consent em FHIR R4).
/// Os textos decifrados só existem durante a montagem; o ZIP sai daqui direto para a cifra.
/// </summary>
internal static class PackageBuilder
{
    public const string ReadmeEntry = "leia-me.html";
    public const string DataEntry = "dados.json";
    public const string FhirEntry = "fhir-bundle.json";
    public const string Format = "careflow.pacote-do-titular";
    public const int FormatVersion = 1;

    public static byte[] Build(SubjectRequest request, IReadOnlyDictionary<string, byte[]> sections, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sections);

        var data = Data(request, sections, generatedAt);
        var bundle = FhirBundleBuilder.Build(
            request.Id,
            Section<VaultSection>(sections, SubjectRequestServices.Vault),
            Section<SchedulingSection>(sections, SubjectRequestServices.Scheduling),
            Section<ConsentSection>(sections, SubjectRequestServices.Consent),
            request.PatientRef,
            generatedAt);
        var fhir = FhirBundleBuilder.Serialize(bundle);
        var readme = Encoding.UTF8.GetBytes(PackageReadme.Render(request, sections, generatedAt));

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, ReadmeEntry, readme, generatedAt);
            Add(zip, DataEntry, data, generatedAt);
            Add(zip, FhirEntry, fhir, generatedAt);
        }

        return output.ToArray();
    }

    private static byte[] Data(SubjectRequest request, IReadOnlyDictionary<string, byte[]> sections, DateTimeOffset generatedAt)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("formato", Format);
            writer.WriteNumber("versao", FormatVersion);
            writer.WriteString("geradoEm", generatedAt);
            writer.WriteStartObject("pedido");
            writer.WriteString("id", request.Id);
            writer.WriteString("tipo", RequestNames.Of(request.Kind));
            writer.WriteString("pseudonimo", request.PatientRef);
            writer.WriteString("recebidoEm", request.ReceivedAt);
            writer.WriteString("prazo", request.DueAt);
            writer.WriteEndObject();
            writer.WriteStartObject("secoes");
            foreach (var service in SubjectRequestServices.All)
            {
                if (sections.TryGetValue(service, out var section))
                {
                    using var document = JsonDocument.Parse(section);
                    writer.WritePropertyName(service);
                    document.RootElement.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static T? Section<T>(IReadOnlyDictionary<string, byte[]> sections, string service)
        where T : class =>
        sections.TryGetValue(service, out var json) ? JsonSerializer.Deserialize<T>(json, SubjectRequestAnswers.SectionJson) : null;

    private static void Add(ZipArchive zip, string name, byte[] content, DateTimeOffset timestamp)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = timestamp;
        using var stream = entry.Open();
        stream.Write(content);
    }
}
