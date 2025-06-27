using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Percolator.Identity;

public class PersistentKeyManagementService : IKeyManagementService
{
    private readonly string _keysPath;
    private readonly ICredentialService _credentialService;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new ECParametersJsonConverter(), new ECPointJsonConverter() }
    };
    private readonly ILogger<PersistentKeyManagementService> _logger;

    // A helper record for serializing ECParameters to and from JSON.
    private record SerializableX3dhKeyTriplet(ECParameters IdentityKey, ECParameters SignedPreKey, ECParameters OneTimePreKey);

    public PersistentKeyManagementService(ICredentialService credentialService,
        ILogger<PersistentKeyManagementService> logger)
    {
        _credentialService = credentialService;
        _logger = logger;

        var appDataPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
        var identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
        _keysPath = Path.Combine(identitiesPath, "keys");
        Directory.CreateDirectory(_keysPath);
    }

    public async Task<X3dhKeys> GetOrCreateKeysAsync(string identityName)
    {
        var keyFilePath = Path.Combine(_keysPath, $"{identityName}.keys");
        var fileLock = _fileLocks.GetOrAdd(identityName, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync();
        try
        {
            if (File.Exists(keyFilePath))
            {
                _logger.LogInformation("Key file found for {IdentityName}. Loading keys.", identityName);
                var encryptedBytes = await File.ReadAllBytesAsync(keyFilePath);
                var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
                var serializableKeys = JsonSerializer.Deserialize<SerializableX3dhKeyTriplet>(decryptedBytes, _jsonOptions)!;

                var ikParams = serializableKeys.IdentityKey;
                var ikSigning = ECDsa.Create(ikParams);
                var ikAgreement = ECDiffieHellman.Create(ikParams);

                var spk = ECDiffieHellman.Create();
                spk.ImportParameters(serializableKeys.SignedPreKey);

                var opk = ECDiffieHellman.Create();
                opk.ImportParameters(serializableKeys.OneTimePreKey);

                return new X3dhKeys(ikSigning, ikAgreement, spk, opk);
            }
            else
            {
                _logger.LogInformation("No key file found for {IdentityName}. Creating new keys.", identityName);
                var ikSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var ikAgreement = ECDiffieHellman.Create(ikSigning.ExportParameters(true));
                var spk = await CreatePreKeyAsync();
                var opk = await CreatePreKeyAsync();

                var serializableKeys = new SerializableX3dhKeyTriplet(
                    ikSigning.ExportParameters(true),
                    spk.ExportParameters(true),
                    opk.ExportParameters(true)
                );

                var decryptedBytes = JsonSerializer.SerializeToUtf8Bytes(serializableKeys, _jsonOptions);
                var encryptedBytes = _credentialService.Protect(decryptedBytes);
                await File.WriteAllBytesAsync(keyFilePath, encryptedBytes);
                SetFileSecurity(keyFilePath);
                _logger.LogInformation("New keys created and saved for {IdentityName}", identityName);
                return new X3dhKeys(ikSigning, ikAgreement, spk, opk);
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<X3dhKeys> GetIdentityKeysAsync(string identityName)
    {
        var keyPath = Path.Combine(_keysPath, $"{identityName}.keys");
        var fileLock = _fileLocks.GetOrAdd(identityName, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync();
        try
        {
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException($"Keys for identity '{identityName}' not found.");
            }

            var encryptedBytes = await File.ReadAllBytesAsync(keyPath);
            var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
            var serializableKeys = JsonSerializer.Deserialize<SerializableX3dhKeyTriplet>(decryptedBytes, _jsonOptions)!;

            var ikParams = serializableKeys.IdentityKey;
            var ikSigning = ECDsa.Create(ikParams);
            var ikAgreement = ECDiffieHellman.Create(ikParams);

            var spk = ECDiffieHellman.Create();
            spk.ImportParameters(serializableKeys.SignedPreKey);

            var opk = ECDiffieHellman.Create();
            opk.ImportParameters(serializableKeys.OneTimePreKey);

            return new X3dhKeys(ikSigning, ikAgreement, spk, opk);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private Task<ECDiffieHellman> CreatePreKeyAsync()
    {
        return Task.Run(() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
    }

    private void SetFileSecurity(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var fileSecurity = fileInfo.GetAccessControl();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            fileSecurity.SetOwner(currentUser);
            var rule = new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow);
            fileSecurity.SetAccessRule(rule);
            fileInfo.SetAccessControl(fileSecurity);
        }
    }
}
