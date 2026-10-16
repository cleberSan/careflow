using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

using CareFlow.Demo.Api.Features;
using CareFlow.Demo.Api.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace CareFlow.Demo.IntegrationTests.Infrastructure;

/// <summary>
/// Um Demo de verdade em Kestrel, no loopback, com os serviços do CareFlow trocados por <see cref="FakeCareFlow"/>.
/// Kestrel (e não o TestServer) para valerem o limite de corpo, o endereço de origem e o cabeçalho encaminhado.
/// </summary>
internal sealed class DemoHost : IAsyncDisposable
{
    public const string SiteOrigin = "https://site.teste";

    private static readonly string[] ServiceNames = ["Vault", "Consent", "Scheduling", "Notifications", "Audit", "Triage", "Rights"];

    private readonly WebApplicationFactory<Program> _factory;

    private DemoHost(WebApplicationFactory<Program> factory, Uri baseAddress, string directory)
    {
        _factory = factory;
        BaseAddress = baseAddress;
        Directory = directory;
    }

    public FakeCareFlow CareFlow { get; private init; } = null!;

    public Uri BaseAddress { get; }

    /// <summary>Pasta temporária do teste (interruptor e registro de clínicas).</summary>
    public string Directory { get; }

    public string SwitchFile => Path.Combine(Directory, "demo-switch.json");

    public string LedgerFile => Path.Combine(Directory, "estado", "clinicas.json");

    public IServiceProvider Services => _factory.Services;

    public static DemoHost Start(params (string Key, string Value)[] overrides) => Start(directory: null, overrides);

    public static DemoHost Start(string? directory, params (string Key, string Value)[] overrides)
    {
        directory ??= System.IO.Directory.CreateTempSubdirectory("careflow-demo-it-").FullName;
        var switchFile = Path.Combine(directory, "demo-switch.json");
        if (!File.Exists(switchFile))
        {
            File.WriteAllText(switchFile, """{ "Demo": { "Switch": { "Enabled": true } } }""");
        }

        var careFlow = new FakeCareFlow();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Demo:Tokens:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Demo:SlotCooldown", "00:00:00");
            builder.UseSetting("Demo:CleanupInterval", "00:00:00.200");
            builder.UseSetting("Demo:AllowedOrigins:0", SiteOrigin);
            builder.UseSetting("Demo:SwitchFile", Path.Combine(directory, "demo-switch.json"));
            builder.UseSetting("Demo:LedgerPath", Path.Combine(directory, "estado", "clinicas.json"));
            foreach (var name in ServiceNames)
            {
                builder.UseSetting($"Demo:Services:{name}", $"http://{name.ToLowerInvariant()}.teste/");
            }

            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IServiceAuthenticator, TestAuthenticator>();
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = careFlow));
            });
        });

        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        factory.StartServer();
        var address = new Uri(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        return new DemoHost(factory, address, directory) { CareFlow = careFlow };
    }

    /// <summary>Um visitante: cliente HTTP comum, com o cabeçalho anti-CSRF e, opcionalmente, um X-Forwarded-For.</summary>
    public HttpClient Visitor(string? forwardedFor = null)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Add("X-CareFlow-Demo", "1");
        if (forwardedFor is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        }

        return client;
    }

    public static async Task<SessionView> OpenAsync(HttpClient visitor)
    {
        using var response = await visitor.PostAsync("demo/v1/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<SessionView>(TestContext.Current.CancellationToken))!;
        visitor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return session;
    }

    public async Task SwitchAsync(bool enabled) =>
        await File.WriteAllTextAsync(SwitchFile, $$"""{ "Demo": { "Switch": { "Enabled": {{(enabled ? "true" : "false")}} } } }""", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária; se algo ainda segura um arquivo, o sistema limpa depois.
        }
    }
}

/// <summary>Espera uma condição que depende de trabalho em segundo plano (limpeza, recarga do interruptor).</summary>
internal static class Eventually
{
    public static async Task TrueAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Não aconteceu a tempo: {description}.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public static Task TrueAsync(Func<bool> condition, string description, TimeSpan? timeout = null) =>
        TrueAsync(() => Task.FromResult(condition()), description, timeout);
}
