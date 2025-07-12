using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Identity.Model;
using Percolator.Infrastructure;

namespace Percolator.Identity;

public class FileSystemIdentityStore : IIdentityStore
{
    private readonly string _identitiesPath;
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

    public FileSystemIdentityStore(IOptions<StorageOptions> storageOptions)
    {
        var dataDirectory = storageOptions.Value.Path;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, dataDirectory);
        
        _identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
        Directory.CreateDirectory(_identitiesPath);
    }

    private string GetIdentityPath(string identityName) => Path.Combine(_identitiesPath, $"{identityName}.json");

    public async Task<IdentityRecord?> GetIdentityAsync(string identityName, CancellationToken cancellationToken = default)
    {
        var identityPath = GetIdentityPath(identityName);
        if (!File.Exists(identityPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(identityPath, cancellationToken);
        return JsonSerializer.Deserialize<IdentityRecord>(json);
    }

    public Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = Directory.EnumerateFiles(_identitiesPath, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!);
        return Task.FromResult(names);
    }

    public async Task StoreIdentityAsync(IdentityRecord identity, CancellationToken cancellationToken = default)
    {
        var identityPath = GetIdentityPath(identity.Name);
        var json = JsonSerializer.Serialize(identity, _jsonSerializerOptions);
        await File.WriteAllTextAsync(identityPath, json, cancellationToken);
        SetFileSecurity(identityPath);
    }

    public Task<bool> IdentityExistsAsync(string identityName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(GetIdentityPath(identityName)));
    }

    private void SetFileSecurity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var fileInfo = new FileInfo(path);
            var fileSecurity = new FileSecurity();
            var currentUser = WindowsIdentity.GetCurrent();

            fileSecurity.SetOwner(currentUser.User!);

            // Disable inheritance and remove existing inherited rules
            fileSecurity.SetAccessRuleProtection(true, false);

            fileSecurity.AddAccessRule(new FileSystemAccessRule(
                currentUser.User!,
                FileSystemRights.FullControl,
                AccessControlType.Allow));

            fileInfo.SetAccessControl(fileSecurity);
        }
    }
}
