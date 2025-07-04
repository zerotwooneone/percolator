using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Percolator.Identity;

public class PersistentKeyManagementService : IKeyManagementService
{
    private readonly ICredentialService _credentialService;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
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
        var basePath = IdentityPathHelper.GetBasePath(identityName);
        var keysPath = Path.Combine(basePath, "keys");
        Directory.CreateDirectory(keysPath);
        var keyFilePath = Path.Combine(keysPath, $"{identityName}.json");
        var fileLock = _fileLocks.GetOrAdd(keyFilePath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync();
        try
        {
            if (File.Exists(keyFilePath))
            {
                _logger.LogInformation("Key file found for {IdentityName}. Loading keys.", identityName);
                var encryptedBytes = await File.ReadAllBytesAsync(keyFilePath);
                var decryptedBytes = _credentialService.Unprotect(encryptedBytes);
                var keyContainer = JsonSerializer.Deserialize<KeyContainer>(decryptedBytes, _jsonOptions)!;

                var ikSigning = ECDsa.Create(keyContainer.IdentitySigningKey);
                var ikAgreement = ECDiffieHellman.Create(keyContainer.IdentityAgreementKey);
                var spk = ECDiffieHellman.Create(keyContainer.SignedPreKey);
                var otps = keyContainer.OneTimePreKeys.Select(ECDiffieHellman.Create).ToArray();

                return new X3dhKeys(ikSigning, ikAgreement, spk, otps);
            }
            else
            {
                _logger.LogInformation("No key file found for {IdentityName}. Creating new keys.", identityName);
                var ikSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var ikAgreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var spk = await CreatePreKeyAsync();
                var oneTimePreKeys = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => CreatePreKeyAsync()));

                var container = new KeyContainer(
                    ikSigning.ExportParameters(true),
                    ikAgreement.ExportParameters(true),
                    spk.ExportParameters(true),
                    oneTimePreKeys.Select(k => k.ExportParameters(true)).ToArray()
                );

                var decryptedBytes = JsonSerializer.SerializeToUtf8Bytes(container, _jsonOptions);
                var encryptedBytes = _credentialService.Protect(decryptedBytes);
                await File.WriteAllBytesAsync(keyFilePath, encryptedBytes);
                SetFileSecurity(keyFilePath);
                _logger.LogInformation("New keys created and saved for {IdentityName}", identityName);
                return new X3dhKeys(ikSigning, ikAgreement, spk, oneTimePreKeys);
            }
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

    private void SetFileSecurity(string path)
    {
        var fileInfo = new FileInfo(path);
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
