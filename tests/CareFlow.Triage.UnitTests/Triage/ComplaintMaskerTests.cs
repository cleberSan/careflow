using CareFlow.Triage.Domain.Pseudonymization;

namespace CareFlow.Triage.UnitTests.Triage;

public sealed class ComplaintMaskerTests
{
    private static readonly KnownIdentity Ana = KnownIdentity.FromFullName("Ana Beatriz da Conceição Souza");

    [Fact]
    public void Mask_ComplaintStuffedWithPersonalData_LeavesNoneOfIt()
    {
        // Arrange
        const string complaint =
            "Meu nome é Ana Beatriz Souza, CPF 123.456.789-09, nasci em 12/03/1980 e tenho 46 anos. "
            + "Moro na Rua das Flores 120, apto 31, CEP 01310-100. Telefone (11) 98765-4321, e-mail ana.souza@example.com, "
            + "insta @anabia. Minha filha Júlia me trouxe. Carteirinha 8765 4321 0987 654. Desde o dia 3 estou com tosse e febre de 38,5.";

        // Act
        var masked = ComplaintMasker.Mask(complaint, Ana);

        // Assert
        foreach (var personal in new[]
        {
            "Ana", "Beatriz", "Souza", "123.456.789-09", "12/03/1980", "46 anos", "Flores", "01310-100", "98765-4321",
            "ana.souza@example.com", "@anabia", "Júlia", "8765 4321 0987 654", "dia 3",
        })
        {
            Assert.DoesNotContain(personal, masked.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("tosse e febre de 38,5", masked.Text, StringComparison.Ordinal);
        Assert.True(masked.Counts[MaskKind.Name] >= 2);
        Assert.Equal(1, masked.Counts[MaskKind.Cpf]);
        Assert.Equal(1, masked.Counts[MaskKind.Email]);
        Assert.Equal(1, masked.Counts[MaskKind.Handle]);
        Assert.Equal(1, masked.Counts[MaskKind.Age]);
        Assert.True(masked.Counts[MaskKind.Date] >= 2);
        Assert.True(masked.Total >= 10);
    }

    [Theory]
    [InlineData("pressão 120/80 e febre de 39.5 há 3 dias", "pressão 120/80 e febre de 39.5 há 3 dias")]
    [InlineData("dor nota 8 de 10 há 2 anos", "dor nota 8 de 10 há 2 anos")]
    [InlineData("Tomei 2 comprimidos às 14h", "Tomei 2 comprimidos às 14h")]
    public void Mask_KeepsClinicalNumbers(string complaint, string expected)
    {
        // Act
        var masked = ComplaintMasker.Mask(complaint, KnownIdentity.None);

        // Assert
        Assert.Equal(expected, masked.Text);
        Assert.Equal(0, masked.Total);
        Assert.Equal(0, masked.MaskedShare);
    }

    [Theory]
    [InlineData("ligue 98765-4321", "[telefone]")]
    [InlineData("whats +55 11 98765-4321", "[telefone]")]
    [InlineData("cpf 12345678909", "[cpf]")]
    [InlineData("veja www.minhaclinica.com.br/ficha", "[link]")]
    [InlineData("site https://example.com/x?y=1", "[link]")]
    [InlineData("comecou em 5 de março de 2026", "[data]")]
    [InlineData("desde janeiro de 2025", "[data]")]
    [InlineData("exame em 2026-09-30", "[data]")]
    [InlineData("rg 12.345.678-9", "[numero]")]
    [InlineData("tenho 47 anos", "[idade]")]
    [InlineData("47 anos de idade", "[idade]")]
    [InlineData("ela tem 7 anos", "[idade]")]
    [InlineData("na Avenida Paulista 1000", "[endereco]")]
    [InlineData("a Dra. Helena Prado pediu", "[nome]")]
    [InlineData("meu marido Carlos Eduardo da Silva acha", "[nome]")]
    [InlineData("me chamo Joana", "[nome]")]
    [InlineData("att, Roberto", "[nome]")]
    public void Mask_ReplacesEachKindWithItsPlaceholder(string complaint, string placeholder)
    {
        // Act
        var masked = ComplaintMasker.Mask(complaint, KnownIdentity.None);

        // Assert
        Assert.Contains(placeholder, masked.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("moro na Rua das Acácias, 1234, apto 56. Dor nas costas.", "1234")]
    [InlineData("moro na Rua das Acácias, nº 87. Tosse.", "87")]
    [InlineData("casa número 340, perto da praça. Tosse.", "340")]
    [InlineData("Av. Brasil, 15B, tosse.", "15B")]
    public void Mask_HouseNumbers_NeverSurvive(string complaint, string number)
    {
        // Act
        var masked = ComplaintMasker.Mask(complaint, KnownIdentity.None);

        // Assert
        Assert.DoesNotContain(number, masked.Text, StringComparison.Ordinal);
        Assert.Contains("[endereco]", masked.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_AdjacentNameParts_BecomeOnePlaceholder()
    {
        // Act
        var masked = ComplaintMasker.Mask("sou a mãe da Alice Rocha, com febre", KnownIdentity.FromFullName("Alice Sintetica Rocha"));

        // Assert
        Assert.Equal("sou a mãe da [nome], com febre", masked.Text);
        Assert.Equal(1, masked.Counts[MaskKind.Name]);
    }

    [Theory]
    [InlineData("ligue 11987654321")]
    [InlineData("ligue 1198765432")]
    public void Mask_BareDigitRuns_NeverSurvive(string complaint)
    {
        // Act: onze dígitos podem ser CPF ou celular; tanto faz o marcador, o número não passa.
        var masked = ComplaintMasker.Mask(complaint, KnownIdentity.None);

        // Assert
        Assert.DoesNotContain(masked.Text, char.IsAsciiDigit);
    }

    [Fact]
    public void Mask_KnownNames_AreMatchedWithoutAccentsOrCaseAndOnlyAsWholeWords()
    {
        // Arrange
        var identity = new KnownIdentity(["Conceição", "da", "Lu", "  "]);

        // Act
        var masked = ComplaintMasker.Mask("conceicao com dor; CONCEIÇÃO piorou; conceicaozinha não; da Lu", identity);

        // Assert
        Assert.Equal("[nome] com dor; [nome] piorou; conceicaozinha não; da Lu", masked.Text);
        Assert.Equal(2, masked.Counts[MaskKind.Name]);
    }

    [Fact]
    public void Mask_MostlyIdentifiers_ReportsHighMaskedShare()
    {
        // Act
        var masked = ComplaintMasker.Mask("ana.souza@example.com 123.456.789-09 (11) 98765-4321 dor", KnownIdentity.None);

        // Assert
        Assert.True(masked.MaskedShare > 0.5);
    }

    [Fact]
    public void Mask_EmptyText_HasZeroShare()
    {
        var masked = ComplaintMasker.Mask("   ", KnownIdentity.None);

        Assert.Equal(0, masked.MaskedShare);
        Assert.Equal("   ", masked.Text);
    }

    [Fact]
    public void KnownIdentity_HidesNamesInToStringAndIgnoresBlankNames()
    {
        Assert.Equal("KnownIdentity { *** }", Ana.ToString());
        Assert.Same(KnownIdentity.None, KnownIdentity.FromFullName(" "));
        Assert.Equal(["Ana", "Beatriz", "da", "Conceição", "Souza"], Ana.NameParts);
    }

    [Theory]
    [InlineData(MaskKind.Name, "[nome]")]
    [InlineData(MaskKind.Cpf, "[cpf]")]
    [InlineData(MaskKind.Phone, "[telefone]")]
    [InlineData(MaskKind.Email, "[email]")]
    [InlineData(MaskKind.Link, "[link]")]
    [InlineData(MaskKind.Handle, "[usuario]")]
    [InlineData(MaskKind.Date, "[data]")]
    [InlineData(MaskKind.Age, "[idade]")]
    [InlineData(MaskKind.Address, "[endereco]")]
    [InlineData(MaskKind.Number, "[numero]")]
    public void MaskKind_PlaceholderAndSlug(MaskKind kind, string placeholder)
    {
        Assert.Equal(placeholder, kind.Placeholder());
        Assert.Equal(kind.ToString().ToLowerInvariant(), kind.Slug());
    }

    [Fact]
    public void MaskKind_Unknown_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ((MaskKind)99).Placeholder());
}
