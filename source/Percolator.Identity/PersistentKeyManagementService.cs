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

    internal record KeyContainer(ECParameters IdentitySigningKey, ECParameters IdentityAgreementKey, ECParameters SignedPreKey, ECParameters[] OneTimePreKeys);

    public async Task<X3dhKeys> GetOrCreateKeysAsync(string identityName)
    {
        try
        {
            return await GetKeysAsync(identityName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogInformation(ex, "No existing keys found for {IdentityName}. A new set will be created.", identityName);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            _logger.LogError(ex, "Failed to load or decrypt existing keys for {IdentityName}. A new set will be created.", identityName);
        }

        return await CreateKeysAsync(identityName);
    }

    public async Task<X3dhKeys> GetKeysAsync(string identityName)
    {
        var keyFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", identityName, "keys.json");

        var encryptedBytes = await File.ReadAllBytesAsync(keyFilePath);
        var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
        var keyContainer = JsonSerializer.Deserialize<KeyContainer>(decryptedBytes, s_jsonOptions);

        if (keyContainer is null)
        {
            throw new JsonException("Key file is corrupt or empty as it deserialized to null.");
        }

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

    public async Task<X3dhKeys> CreateKeysAsync(string identityName)
    {
        _logger.LogInformation("No existing keys found for {IdentityName}. Creating a new set.", identityName);

        // Create and save new keys
        var newIkSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var newIkAgreement = await CreatePreKeyAsync();
        var newSpk = await CreatePreKeyAsync();
        var newOtps = new List<ECDiffieHellman>();
        for (var i = 0; i < 10; i++)
        {
            newOtps.Add(await CreatePreKeyAsync());
        }

        var newKeyContainer = new KeyContainer(
            newIkSigning.ExportParameters(true),
            newIkAgreement.ExportParameters(true),
            newSpk.ExportParameters(true),
            newOtps.Select(k => k.ExportParameters(true)).ToArray()
        );

        var keyFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", identityName, "keys.json");
        var newDecryptedBytes = JsonSerializer.SerializeToUtf8Bytes(newKeyContainer, s_jsonOptions);
        var newEncryptedBytes = _credentialService.Protect(newDecryptedBytes);

        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        await File.WriteAllBytesAsync(keyFilePath, newEncryptedBytes);
        SetFileSecurity(keyFilePath);

        _logger.LogInformation("New keys created and saved for {IdentityName}", identityName);

        return new X3dhKeys(newIkSigning, newIkAgreement, newSpk, newOtps.ToArray());
    }

    private async Task<ECDiffieHellman> CreatePreKeyAsync()
    {
        return await Task.FromResult(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
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
