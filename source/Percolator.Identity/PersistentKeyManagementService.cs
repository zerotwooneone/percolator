using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Percolator.Identity;

public class PersistentKeyManagementService : IKeyManagementService
{
    private readonly string _keysPath;
    private readonly ICredentialService _credentialService;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly ILogger<PersistentKeyManagementService> _logger;

    // A helper record for serializing ECParameters to and from JSON.
    private record SerializableX3dhKeyTriplet(ECParameters IdentityKey, ECParameters SignedPreKey, ECParameters OneTimePreKey);

    public PersistentKeyManagementService(ICredentialService credentialService,
        ILogger<PersistentKeyManagementService> logger)
    {
        _credentialService = credentialService;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ECParametersJsonConverter(), new ECPointJsonConverter() }
        };

        _serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ECParametersJsonConverter(), new ECPointJsonConverter() }
        };

        var appDataPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
        var identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
        _keysPath = Path.Combine(identitiesPath, "keys");
        Directory.CreateDirectory(_keysPath);
    }

    public X3dhKeys GetOrCreateKeys(string identityName)
    {
        var keyFilePath = Path.Combine(_keysPath, $"{identityName}.keys");

        if (File.Exists(keyFilePath))
        {
            _logger.LogInformation("Key file found for {IdentityName}. Loading keys.", identityName);
            var encryptedBytes = File.ReadAllBytes(keyFilePath);
            var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
            var serializableKeys = JsonSerializer.Deserialize<SerializableX3dhKeyTriplet>(decryptedBytes, _jsonOptions)!;

            var ik = ECDiffieHellman.Create();
            ik.ImportParameters(serializableKeys.IdentityKey);

            var spk = ECDiffieHellman.Create();
            spk.ImportParameters(serializableKeys.SignedPreKey);

            var opk = ECDiffieHellman.Create();
            opk.ImportParameters(serializableKeys.OneTimePreKey);

            return new X3dhKeys(ik, spk, opk);
        }
        else
        {
            var ik = CreateKey();
            var spk = CreateKey();
            var opk = CreateKey();

            var serializableKeys = new SerializableX3dhKeyTriplet(
                ik.ExportParameters(true),
                spk.ExportParameters(true),
                opk.ExportParameters(true)
            );

            var decryptedBytes = JsonSerializer.SerializeToUtf8Bytes(serializableKeys, _jsonOptions);
            var encryptedBytes = _credentialService.Protect(decryptedBytes);
            File.WriteAllBytes(keyFilePath, encryptedBytes);
            SetFileSecurity(keyFilePath);
            _logger.LogInformation("New keys created and saved for {IdentityName}", identityName);
            return new X3dhKeys(ik, spk, opk);
        }
    }

    public X3dhKeys GetIdentityKeys(string identityName)
    {
        var keyPath = Path.Combine(_keysPath, $"{identityName}.keys");
        if (!File.Exists(keyPath))
        {
            throw new InvalidOperationException($"Keys for identity '{identityName}' not found.");
        }

        var encryptedBytes = File.ReadAllBytes(keyPath);
        var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
        var serializableKeys = JsonSerializer.Deserialize<SerializableX3dhKeyTriplet>(decryptedBytes, _jsonOptions)!;

        var ik = ECDiffieHellman.Create();
        ik.ImportParameters(serializableKeys.IdentityKey);

        var spk = ECDiffieHellman.Create();
        spk.ImportParameters(serializableKeys.SignedPreKey);

        var opk = ECDiffieHellman.Create();
        opk.ImportParameters(serializableKeys.OneTimePreKey);

        return new X3dhKeys(ik, spk, opk);
    }

    private ECDiffieHellman CreateKey()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
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
