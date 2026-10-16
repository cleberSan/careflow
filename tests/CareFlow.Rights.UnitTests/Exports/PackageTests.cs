using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.Rights.Api.Common.Cryptography;
using CareFlow.Rights.Api.Features.Downloads;
using CareFlow.Rights.Api.Features.Exports;
using CareFlow.Rights.Api.Features.Requests;
using CareFlow.Rights.Domain.Requests;

using Microsoft.Extensions.Options;

namespace CareFlow.Rights.UnitTests.Exports;

public sealed class PackageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] Tags = ["a", "b"];

    private static RightsKeyRing Keys(string? kek = null)
    {
        using var notifications = SealedBox.CreateKeyPair();
        return new RightsKeyRing(
            Options.Create(new RightsKeyOptions { KeyEncryptionKey = kek ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }),
            Options.Create(new IdentityVerificationOptions { NotificationsPublicKey = SealedBox.ExportPublicKey(notifications) }));
    }

    private static SubjectRequest Request(SubjectRequestKind kind = SubjectRequestKind.Access)
    {
        var request = SubjectRequest.Receive(Guid.NewGuid(), Guid.NewGuid(), kind, _ => new byte[32], Now).Value;
        request.ConfirmIdentity(new byte[32], Now);
        return request;
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, SubjectRequestAnswers.SectionJson);

    [Fact]
    public void Readme_EncodesEverything_SoAStoredNameCannotBecomeMarkup()
    {
        // Arrange
        var request = Request();
        var hostile = "<script>alert(1)</script><img src=x onerror=alert(2)>";
        var sections = new Dictionary<string, byte[]>
        {
            [SubjectRequestServices.Vault] = Json(new VaultSection(request.PatientRef, hostile, "52998224725", new DateOnly(1990, 1, 1), null, null, Now, Now)),
            [SubjectRequestServices.Audit] = Json(new { entries = Array.Empty<object>() }),
        };

        // Act
        var html = PackageReadme.Render(request, sections, Now);

        // Assert
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("Nada registrado.", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Cadastro</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_RendersTablesListsAndScalars()
    {
        var request = Request();
        var sections = new Dictionary<string, byte[]>
        {
            [SubjectRequestServices.Notifications] = Json(new { items = new object[] { new { kind = "reminder", sent = true }, new { kind = "cancel", sent = false, extra = (string?)null } }, tags = Tags, count = 2 }),
        };

        var html = PackageReadme.Render(request, sections, Now);

        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<td>sim</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>não</td>", html, StringComparison.Ordinal);
        Assert.Contains("<li>a</li>", html, StringComparison.Ordinal);
        Assert.Contains("<dd>2</dd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_HasTheThreeFiles_WithEverySection()
    {
        // Arrange
        var request = Request(SubjectRequestKind.Portability);
        var sections = SubjectRequestServices.All.ToDictionary(s => s, s => Json(new { service = s }));
        sections[SubjectRequestServices.Vault] = Json(new VaultSection(request.PatientRef, "Paciente Sintetico", "52998224725", new DateOnly(1990, 1, 1), null, null, Now, Now));

        // Act
        var bytes = PackageBuilder.Build(request, sections, Now);

        // Assert
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal([PackageBuilder.ReadmeEntry, PackageBuilder.DataEntry, PackageBuilder.FhirEntry], zip.Entries.Select(e => e.FullName).ToArray());
        using var data = JsonDocument.Parse(zip.GetEntry(PackageBuilder.DataEntry)!.Open());
        Assert.Equal(PackageBuilder.Format, data.RootElement.GetProperty("formato").GetString());
        Assert.Equal("portability", data.RootElement.GetProperty("pedido").GetProperty("tipo").GetString());
        Assert.Equal(SubjectRequestServices.All, data.RootElement.GetProperty("secoes").EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Cipher_RoundTrips_AndIsBoundToTheRequest()
    {
        using var keys = Keys();
        var cipher = new PackageCipher(keys);
        var requestId = Guid.NewGuid();
        var zip = Encoding.UTF8.GetBytes("conteudo sintetico");

        var (content, wrappedKey) = cipher.Seal(requestId, zip);

        Assert.Equal(zip, cipher.Open(requestId, content, wrappedKey));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(Guid.NewGuid(), content, wrappedKey));
        content[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(requestId, content, wrappedKey));
    }

    [Fact]
    public void KeyRing_WrapsPerContext_AndHashesCodesPerRequest()
    {
        using var keys = Keys();
        using var otherKeys = Keys();
        var requestId = Guid.NewGuid();
        var wrapped = keys.Wrap("segredo"u8, RightsKeyRing.RecipientKeyContext(requestId));

        Assert.Equal("segredo"u8.ToArray(), keys.Unwrap(wrapped, RightsKeyRing.RecipientKeyContext(requestId)));
        Assert.ThrowsAny<CryptographicException>(() => keys.Unwrap(wrapped, RightsKeyRing.PackageKeyContext(requestId)));
        Assert.ThrowsAny<CryptographicException>(() => otherKeys.Unwrap(wrapped, RightsKeyRing.RecipientKeyContext(requestId)));
        Assert.ThrowsAny<CryptographicException>(() => keys.Unwrap([1, 2, 3], RightsKeyRing.RecipientKeyContext(requestId)));

        Assert.Equal(keys.CodeHash(requestId, "123456"), keys.CodeHash(requestId, "123456"));
        Assert.NotEqual(keys.CodeHash(requestId, "123456"), keys.CodeHash(Guid.NewGuid(), "123456"));
        Assert.NotEqual(keys.CodeHash(requestId, "123456"), otherKeys.CodeHash(requestId, "123456"));
        Assert.Matches("^[0-9]{6}$", RightsKeyRing.NewCode());
    }

    [Theory]
    [InlineData("nao-e-base64!")]
    [InlineData("AAAA")]
    public void KeyRing_InvalidKey_FailsAtStartup(string kek)
    {
        Assert.Throws<InvalidOperationException>(() => Keys(kek));
    }

    [Fact]
    public void KeyRing_InvalidNotificationsKey_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => new RightsKeyRing(
            Options.Create(new RightsKeyOptions { KeyEncryptionKey = Convert.ToBase64String(new byte[32]) }),
            Options.Create(new IdentityVerificationOptions { NotificationsPublicKey = "AAAA" })));
    }

    [Fact]
    public void DownloadToken_RoundTrips_AndRejectsTampering()
    {
        var tenant = Guid.NewGuid();
        var (token, hash) = DownloadToken.Create(tenant);

        Assert.True(DownloadToken.TryParse(token, out var parsedTenant, out var parsedHash));
        Assert.Equal(tenant, parsedTenant);
        Assert.Equal(hash, parsedHash);
        Assert.Equal(64, token.Length);
        Assert.False(DownloadToken.TryParse(token[..^2], out _, out _));
        Assert.False(DownloadToken.TryParse(null, out _, out _));
        Assert.False(DownloadToken.TryParse(new string('A', 200), out _, out _));
        Assert.False(DownloadToken.TryParse(DownloadToken.Create(Guid.Empty).Token, out _, out _));
    }

    [Fact]
    public void Receipt_UsesThePolicy_AndCountsWhatEachServiceReported()
    {
        // Arrange
        var request = Request(SubjectRequestKind.Erasure);
        request.ApplyPolicy(RetentionPolicy.Version);
        request.Complete(Now);
        var lines = JsonSerializer.Serialize(new[] { new ErasureLine(RetentionPolicy.Items.SchedulingPastAppointments, RetentionActions.Retained, 4) }, WebJson);
        var answers = new[]
        {
            SubjectRequestAnswer.Create(request.TenantId, request.Id, SubjectRequestServices.Scheduling, SubjectRequestOutcomes.Erased, null, lines, Now),
        };

        // Act
        var receipt = RequestView.Receipt(request, answers)!;

        // Assert
        Assert.Equal(RetentionPolicy.Rules.Count, receipt.Lines.Count);
        var past = receipt.Lines.Single(l => l.Item == RetentionPolicy.Items.SchedulingPastAppointments);
        Assert.Equal(4, past.Records);
        Assert.Equal(Now.AddDays(RetentionPolicy.FiveYears), past.RetainedUntil);
        Assert.Equal(0, receipt.Lines.Single(l => l.Item == RetentionPolicy.Items.VaultPersonalData).Records);
        Assert.Null(RequestView.Receipt(Request(), answers));
    }

    [Fact]
    public void Names_AreStable()
    {
        Assert.Equal(["received", "in-progress", "completed", "denied"], Enum.GetValues<SubjectRequestStatus>().Select(RequestNames.Of).ToArray());
        Assert.Equal(["no-contact", "code-not-delivered", "identity-not-confirmed", "subject-not-found"], Enum.GetValues<DenialReason>().Select(RequestNames.Of).ToArray());
        Assert.All(SubjectRequestKinds.All, kind => Assert.True(RequestNames.TryParseKind(kind, out var parsed) && RequestNames.Of(parsed) == kind));
        Assert.False(RequestNames.TryParseKind("x", out _));
    }
}
