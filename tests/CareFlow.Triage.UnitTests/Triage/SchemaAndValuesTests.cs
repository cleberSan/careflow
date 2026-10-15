using System.Text.Json;

using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Priorities;
using CareFlow.Triage.Domain.Suggestions;
using CareFlow.Triage.Domain.Text;

namespace CareFlow.Triage.UnitTests.Triage;

public sealed class SuggestionSchemaTests
{
    [Fact]
    public void Parse_ValidResponse_ReturnsSuggestion()
    {
        var result = SuggestionSchema.Parse("""{"priority":"urgent","rationale":"  Febre alta há dois dias e prostração.  "}""");

        Assert.True(result.IsSuccess);
        Assert.Equal(TriagePriority.Urgent, result.Value.Priority);
        Assert.Equal("Febre alta há dois dias e prostração.", result.Value.Rationale);
    }

    [Theory]
    [InlineData(null, "Triagem.RespostaNaoEJson")]
    [InlineData("", "Triagem.RespostaNaoEJson")]
    [InlineData("urgent", "Triagem.RespostaNaoEJson")]
    [InlineData("{\"priority\":\"urgent\",", "Triagem.RespostaNaoEJson")]
    [InlineData("[\"urgent\"]", "Triagem.RespostaNaoEObjeto")]
    [InlineData("{\"priority\":\"urgent\",\"rationale\":\"Febre alta há dois dias.\",\"diagnosis\":\"gripe\"}", "Triagem.RespostaComCampoExtra")]
    [InlineData("{\"priority\":\"critical\",\"rationale\":\"Febre alta há dois dias.\"}", "Triagem.RespostaComPrioridadeInvalida")]
    [InlineData("{\"priority\":3,\"rationale\":\"Febre alta há dois dias.\"}", "Triagem.RespostaComPrioridadeInvalida")]
    [InlineData("{\"priority\":\"urgent\",\"priority\":\"non-urgent\",\"rationale\":\"Febre alta há dois dias.\"}", "Triagem.RespostaComPrioridadeInvalida")]
    [InlineData("{\"rationale\":\"Febre alta há dois dias.\"}", "Triagem.RespostaComPrioridadeInvalida")]
    [InlineData("{\"priority\":\"urgent\"}", "Triagem.RespostaComJustificativaInvalida")]
    [InlineData("{\"priority\":\"urgent\",\"rationale\":\"curta\"}", "Triagem.RespostaComJustificativaInvalida")]
    [InlineData("{\"priority\":\"urgent\",\"rationale\":\"linha um\\nlinha dois do texto\"}", "Triagem.RespostaComJustificativaInvalida")]
    [InlineData("{\"priority\":\"urgent\",\"rationale\":null}", "Triagem.RespostaComJustificativaInvalida")]
    [InlineData("{\"priority\":\"urgent\",\"rationale\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}", "Triagem.RespostaNaoEJson")]
    public void Parse_OutOfSchema_IsRejected(string? response, string code)
    {
        Assert.Equal(code, SuggestionSchema.Parse(response).Error.Code);
    }

    [Fact]
    public void Parse_TooLong_IsRejectedBeforeParsing()
    {
        var response = "{\"priority\":\"urgent\",\"rationale\":\"" + new string('a', SuggestionSchema.MaxResponseLength) + "\"}";

        Assert.Equal(SuggestionSchema.NotJson, SuggestionSchema.Parse(response).Error);
    }

    [Fact]
    public void Parse_RationaleOverLimit_IsRejected()
    {
        var response = "{\"priority\":\"urgent\",\"rationale\":\"" + new string('a', SuggestionSchema.MaxRationaleLength + 1) + "\"}";

        Assert.Equal(SuggestionSchema.InvalidRationale, SuggestionSchema.Parse(response).Error);
    }

    [Fact]
    public void JsonSchema_ClosesTheObjectAndListsThePriorities()
    {
        using var schema = JsonDocument.Parse(SuggestionSchema.JsonSchema);
        var root = schema.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["priority", "rationale"], root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(TriagePriorityNames.All, root.GetProperty("properties").GetProperty("priority").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!));
    }
}

public sealed class ValueTests
{
    [Theory]
    [InlineData(TriagePriority.NonUrgent, "non-urgent")]
    [InlineData(TriagePriority.LessUrgent, "less-urgent")]
    [InlineData(TriagePriority.Urgent, "urgent")]
    [InlineData(TriagePriority.Emergency, "emergency")]
    public void Priority_SlugRoundTrips(TriagePriority priority, string slug)
    {
        Assert.Equal(slug, priority.Slug());
        Assert.True(TriagePriorityNames.TryParse(slug, out var parsed));
        Assert.Equal(priority, parsed);
    }

    [Fact]
    public void Priority_OrderAndUnknowns()
    {
        Assert.True(TriagePriority.Emergency > TriagePriority.Urgent);
        Assert.Equal(TriagePriority.Urgent, TriagePriorityNames.Max(TriagePriority.LessUrgent, TriagePriority.Urgent));
        Assert.Equal(TriagePriority.Urgent, TriagePriorityNames.Max(TriagePriority.Urgent, TriagePriority.NonUrgent));
        Assert.False(TriagePriorityNames.TryParse("Urgent", out _));
        Assert.False(TriagePriorityNames.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ((TriagePriority)9).Slug());
    }

    [Theory]
    [InlineData(0, "0-1", true)]
    [InlineData(1, "0-1", true)]
    [InlineData(2, "2-11", false)]
    [InlineData(17, "12-17", false)]
    [InlineData(18, "18-39", false)]
    [InlineData(59, "40-59", false)]
    [InlineData(79, "60-79", false)]
    [InlineData(103, "80+", false)]
    public void AgeBand_FromAge(int age, string name, bool infant)
    {
        var band = AgeBand.FromAge(age);

        Assert.Equal(name, band.Name);
        Assert.Equal(name, band.ToString());
        Assert.Equal(infant, band.IsInfant);
        Assert.True(AgeBand.TryParse(name, out var parsed));
        Assert.Equal(band, parsed);
    }

    [Fact]
    public void AgeBand_FromBirthDate_CountsBirthdays()
    {
        var today = new DateOnly(2026, 10, 15);

        Assert.Equal("40-59", AgeBand.FromBirthDate(new DateOnly(1986, 10, 15), today).Name);
        Assert.Equal("18-39", AgeBand.FromBirthDate(new DateOnly(1986, 10, 16), today).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => AgeBand.FromBirthDate(today.AddDays(1), today));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgeBand.FromAge(-1));
        Assert.False(AgeBand.TryParse("adulto", out _));
        Assert.Equal(7, AgeBand.Names.Count);
    }

    [Fact]
    public void Folding_KeepsLengthAndRemovesAccents()
    {
        const string text = "Ação ÉRICA çé ñ ß";

        var folded = TextFolding.Fold(text);

        Assert.Equal(text.Length, folded.Length);
        Assert.Equal("acao erica ce n ß", folded);
    }

    [Fact]
    public void Clean_RemovesInvisibleAndControlCharactersAndCollapsesSpaces()
    {
        var cleaned = TextFolding.Clean("  dor​ no\tpeito\r\n\r\n agora\u0007 🙂  ");

        Assert.Equal("dor no peito agora", cleaned);
    }
}
