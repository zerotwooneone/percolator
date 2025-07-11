using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Percolator.Identity;

public class PersistentKeyManagementService : IKeyManagementService
{
    private readonly ICredentialService _credentialService;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new ECParametersJsonConverter(), new ECPointJsonConverter() }
    };
    private readonly ILogger<PersistentKeyManagementService> _logger;

    public PersistentKeyManagementService(
        ICredentialService credentialService,
        ILogger<PersistentKeyManagementService> logger)
    {
        _credentialService = credentialService;
        _logger = logger;
    }

    private record KeyContainer(ECParameters IdentitySigningKey, ECParameters IdentityAgreementKey, ECParameters SignedPreKey, ECParameters[] OneTimePreKeys);

    public async Task<X3dhKeys> GetOrCreateKeysAsync(string identityName)
    {
        var keyFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", identityName, "keys.json");

        if (File.Exists(keyFilePath))
        {
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(keyFilePath);
                var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
                var keyContainer = JsonSerializer.Deserialize<KeyContainer>(decryptedBytes, s_jsonOptions);

                if (keyContainer is not null)
                {
                    _logger.LogInformation("Existing keys loaded for {IdentityName}", identityName);
                    var loadedIkSigning = ECDsa.Create(keyContainer.IdentitySigningKey);
                    var loadedIkAgreement = ECDiffieHellman.Create(keyContainer.IdentityAgreementKey);
                    var loadedSpk = ECDiffieHellman.Create(keyContainer.SignedPreKey);
                    var loadedOtps = keyContainer.OneTimePreKeys.Select(p =>
                    {
                        var k = ECDiffieHellman.Create();
                        k.ImportParameters(p);
                        return k;
                    }).ToArray();

                    return new X3dhKeys(loadedIkSigning, loadedIkAgreement, loadedSpk, loadedOtps);
                }
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException)
            {
                _logger.LogError(ex, "Failed to load or decrypt existing keys for {IdentityName}. A new set will be created.", identityName);
            }
        }

        _logger.LogInformation("No existing keys found for {IdentityName}. Creating a new set.", identityName);

        var newIkSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var newIkAgreement = await CreatePreKeyAsync();
        var newSpk = await CreatePreKeyAsync();
        var newOneTimePreKeys = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CreatePreKeyAsync()));

        var newKeyContainer = new KeyContainer(
            newIkSigning.ExportParameters(true),
            newIkAgreement.ExportParameters(true),
            newSpk.ExportParameters(true),
            newOneTimePreKeys.Select(k => k.ExportParameters(true)).ToArray()
        );

        var newDecryptedBytes = JsonSerializer.SerializeToUtf8Bytes(newKeyContainer, s_jsonOptions);
        var newEncryptedBytes = _credentialService.Protect(newDecryptedBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        await File.WriteAllBytesAsync(keyFilePath, newEncryptedBytes);
        SetFileSecurity(keyFilePath);
        _logger.LogInformation("New keys created and saved for {IdentityName}", identityName);

        // Return the newly created keys
        return new X3dhKeys(newIkSigning, newIkAgreement, newSpk, newOneTimePreKeys);
    }

    private Task<ECDiffieHellman> CreatePreKeyAsync()
    {
        return Task.FromResult(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
    }

    private static void SetFileSecurity(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();
            fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            fileInfo.SetAccessControl(fileSecurity);
        }
    }
}
