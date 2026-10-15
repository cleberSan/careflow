using Microsoft.Extensions.Configuration;

namespace CareFlow.Notifications.IntegrationTests.Infrastructure;

/// <summary>
/// Configuração que os testes alteram com a aplicação rodando, como um operador mudaria um appsettings com
/// reload: liga e desliga falhas injetadas sem reiniciar o host.
/// </summary>
public sealed class MutableConfiguration : IConfigurationSource
{
    private readonly MutableProvider _provider = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

    public void Set(IReadOnlyDictionary<string, string?> values)
    {
        _provider.Replace(values);
    }

    private sealed class MutableProvider : ConfigurationProvider
    {
        public void Replace(IReadOnlyDictionary<string, string?> values)
        {
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }
}
