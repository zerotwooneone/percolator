using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Infrastructure.Identity;

public class PersistentKeyManagementService : IKeyManagementService
{
    private readonly ICredentialService _credentialService;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new ECParametersJsonConverter(), new ECPointJsonConverter() }
    };
    private readonly ILogger<PersistentKeyManagementService> _logger;
    private readonly IOptions<CryptographyOptions> _cyptographyOptions;
    private readonly string _percolatorAppDataPath;

    public PersistentKeyManagementService(
        ICredentialService credentialService,
        ILogger<PersistentKeyManagementService> logger,
        IOptions<StorageOptions> storageOptions,
        IOptions<CryptographyOptions> cyptographyOptions)
    {
        _credentialService = credentialService;
        _logger = logger;
        _cyptographyOptions = cyptographyOptions;

        var dataDirectory = storageOptions.Value.Path;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _percolatorAppDataPath = Path.Combine(appDataPath, dataDirectory);
        _logger.LogInformation("Percolator data directory: {PercolatorAppDataPath}!!!!{storageOptionsValuePath}", _percolatorAppDataPath, storageOptions.Value.Path);
    }

    internal record KeyContainer(ECParameters IdentitySigningKey, ECParameters IdentityAgreementKey, ECParameters SignedPreKey);

    public async Task<X3dhKeys?> GetKeysAsync(string identityName)
    {
        var keyFilePath = GetKeyFilePath(identityName);
        if (!File.Exists(keyFilePath))
        {
            return null;
        }

        var encryptedBytes = await File.ReadAllBytesAsync(keyFilePath);
        var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
        var keyContainer = JsonSerializer.Deserialize<KeyContainer>(decryptedBytes, s_jsonOptions);

        if (keyContainer is null)
        {
            throw new JsonException("Key file is corrupt or empty as it deserialized to null.");
        }

        _logger.LogInformation("Existing keys loaded for {IdentityName}", identityName);
        var loadedIkSigning = ECDiffieHellman.Create(keyContainer.IdentitySigningKey);
        var loadedIkAgreement = ECDiffieHellman.Create(keyContainer.IdentityAgreementKey);
        var loadedSpk = ECDiffieHellman.Create(keyContainer.SignedPreKey);

        if (_cyptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Identity Signing Key: {IdentitySigningKey}", Convert.ToBase64String(loadedIkSigning.ExportECPrivateKey()));
            _logger.LogInformation("Identity Agreement Key: {IdentityAgreementKey}", Convert.ToBase64String(loadedIkAgreement.ExportECPrivateKey()));
            _logger.LogInformation("Signed Pre Key: {SignedPreKey}", Convert.ToBase64String(loadedSpk.ExportECPrivateKey()));
        }
        
        return new X3dhKeys(loadedIkSigning, loadedIkAgreement, loadedSpk);
    }

    internal string GetKeyFilePath(string identityName)
    {
        return Path.Combine(_percolatorAppDataPath, identityName, "keys.json");
    }

    public async Task<X3dhKeys> CreateKeysAsync(string identityName)
    {
        _logger.LogInformation("No existing keys found for {IdentityName}. Creating a new set.", identityName);

        // Create and save new keys
        var newIkSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var newIkAgreement = await CreatePreKeyAsync();
        var newSpk = await CreatePreKeyAsync();
        

        var newKeyContainer = new KeyContainer(
            newIkSigning.ExportParameters(true),
            newIkAgreement.ExportParameters(true),
            newSpk.ExportParameters(true)
        );

        var keyFilePath = GetKeyFilePath(identityName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        var file = new FileInfo(keyFilePath);
        _logger.LogInformation("Saving new keys to {Path}", file.DirectoryName);
        Directory.CreateDirectory(Path.GetDirectoryName(file.DirectoryName)!);
        var newDecryptedBytes = JsonSerializer.SerializeToUtf8Bytes(newKeyContainer, s_jsonOptions);
        var newEncryptedBytes = _credentialService.Protect(newDecryptedBytes);

        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        await File.WriteAllBytesAsync(keyFilePath, newEncryptedBytes);
        SetFileSecurity(keyFilePath);

        _logger.LogInformation("New keys created and saved for {IdentityName}", identityName);
        if (_cyptographyOptions.Value.EnableCryptographicMaterialLogging)
        {
            _logger.LogInformation("Identity Signing Key: {IdentitySigningKey}", Convert.ToBase64String(newIkSigning.ExportECPrivateKey()));
            _logger.LogInformation("Identity Agreement Key: {IdentityAgreementKey}", Convert.ToBase64String(newIkAgreement.ExportECPrivateKey()));
            _logger.LogInformation("Signed Pre Key: {SignedPreKey}", Convert.ToBase64String(newSpk.ExportECPrivateKey()));
        }

        return new X3dhKeys(newIkSigning, newIkAgreement, newSpk);
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
