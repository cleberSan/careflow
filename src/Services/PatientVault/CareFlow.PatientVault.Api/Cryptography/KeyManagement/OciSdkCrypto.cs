using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using Oci.Common;
using Oci.Common.Auth;
using Oci.Common.Model;
using Oci.Common.Retry;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using Oci.KeymanagementService.Requests;

namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

/// <summary>
/// Adaptador fino sobre o SDK da Oracle. O cliente só é criado na primeira operação: a autenticação por instance
/// principal fala com o serviço de metadados da VM, que não existe fora da Oracle (CI, migrations). A verificação
/// real é o teste de fumaça depois do deploy, que cadastra um paciente e portanto cifra uma DEK no Vault.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Só roda numa VM da Oracle; coberto pelo teste de fumaça pós-deploy.")]
internal sealed class OciSdkCrypto(IOptions<OciVaultKeyManagementOptions> options) : IOciCrypto, IDisposable
{
    private readonly Lazy<KmsCryptoClient> _client = new(() => Create(options.Value), LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<string> EncryptAsync(string keyId, string plaintextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken)
    {
        var response = await _client.Value.Encrypt(
                new EncryptRequest
                {
                    EncryptDataDetails = new EncryptDataDetails
                    {
                        KeyId = keyId,
                        Plaintext = plaintextBase64,
                        AssociatedData = new Dictionary<string, string>(associatedData, StringComparer.Ordinal),
                    },
                },
                retryConfiguration: null,
                cancellationToken)
            .ConfigureAwait(false);
        return response.EncryptedData.Ciphertext;
    }

    public async Task<string> DecryptAsync(string keyId, string ciphertextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken)
    {
        try
        {
            return await DecryptCoreAsync(keyId, ciphertextBase64, associatedData, cancellationToken).ConfigureAwait(false);
        }
        catch (OciException exception) when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            // Contexto diferente, ciphertext adulterado ou chave apagada: recusa definitiva, como no provedor local.
            throw new CryptographicException("O OCI Vault recusou decifrar a chave de dados.", exception);
        }
    }

    private async Task<string> DecryptCoreAsync(string keyId, string ciphertextBase64, IReadOnlyDictionary<string, string> associatedData, CancellationToken cancellationToken)
    {
        var response = await _client.Value.Decrypt(
                new DecryptRequest
                {
                    DecryptDataDetails = new DecryptDataDetails
                    {
                        KeyId = keyId,
                        Ciphertext = ciphertextBase64,
                        AssociatedData = new Dictionary<string, string>(associatedData, StringComparer.Ordinal),
                    },
                },
                retryConfiguration: null,
                cancellationToken)
            .ConfigureAwait(false);
        return response.DecryptedData.Plaintext;
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static KmsCryptoClient Create(OciVaultKeyManagementOptions options) =>
        new(
            new InstancePrincipalsAuthenticationDetailsProvider(),
            new ClientConfiguration
            {
                TimeoutMillis = 10_000,
                // Repetições do SDK limitadas a 15 s no total; o cache de DEKs evita chamar o Vault a cada requisição.
                RetryConfiguration = new RetryConfiguration { TotalElapsedTimeInSecs = 15 },
            },
            options.CryptoEndpoint!.ToString());
}
