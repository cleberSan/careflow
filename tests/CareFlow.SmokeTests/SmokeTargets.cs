namespace CareFlow.SmokeTests;

internal static class SmokeTargets
{
    /// <summary>O alvo configurado; sem <c>CAREFLOW_SMOKE_URL</c> o teste é pulado.</summary>
    public static SmokeTarget Require()
    {
        var target = SmokeTarget.FromEnvironment();
        Assert.SkipWhen(target is null, "CAREFLOW_SMOKE_URL não definida: o teste de fumaça só roda depois de um deploy.");
        return target!;
    }
}
