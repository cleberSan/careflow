namespace CareFlow.Triage.Domain.Engine;

/// <summary>Uma proteção que agiu nesta triagem: tipo e código, sem trecho do texto.</summary>
public sealed record Guardrail(string Kind, string Code)
{
    public const string Alarm = "alarm";
    public const string Injection = "injection";
    public const string Schema = "schema";
    public const string ClinicalLanguage = "clinical-language";
    public const string Masking = "masking";
    public const string Consent = "consent";
    public const string Provider = "provider";

    public override string ToString() => $"{Kind}:{Code}";
}
