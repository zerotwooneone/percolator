using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Percolator.Identity.Model;
using Identity = Percolator.Identity.Model.Identity;

namespace Percolator.Identity;

public class FileSystemIdentityStore : IIdentityStore
{
    private readonly string _identitiesPath;

    private record IdentityMetadata(string? Nickname);

    public FileSystemIdentityStore()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
        _identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
        Directory.CreateDirectory(_identitiesPath);
    }

    public async Task<Model.Identity?> GetIdentityAsync(string identityName)
    {
        var pfxPath = Path.Combine(_identitiesPath, $"{identityName}.pfx");
        if (!File.Exists(pfxPath))
        {
            return null;
        }

        var pfxBytes = await File.ReadAllBytesAsync(pfxPath);
        var pfxCertificate = new PfxCertificate(pfxBytes);

        var metadataPath = Path.Combine(_identitiesPath, $"{identityName}.json");
        string? nickname = null;
        if (File.Exists(metadataPath))
        {
            var json = await File.ReadAllTextAsync(metadataPath);
            var metadata = JsonSerializer.Deserialize<IdentityMetadata>(json);
            nickname = metadata?.Nickname;
        }

        return new Model.Identity(identityName, pfxCertificate, nickname);
    }

    public Task<IEnumerable<string>> ListIdentityNamesAsync()
    {
        var names = Directory.EnumerateFiles(_identitiesPath, "*.pfx")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!);
        return Task.FromResult(names);
    }

    public async Task StoreIdentityAsync(Model.Identity identity)
    {
        // Store PFX
        var pfxPath = Path.Combine(_identitiesPath, $"{identity.Name}.pfx");
        await File.WriteAllBytesAsync(pfxPath, identity.PfxCertificate.Value);
        SetFileSecurity(pfxPath);

        // Store metadata
        var metadata = new IdentityMetadata(identity.Nickname);
        var metadataPath = Path.Combine(_identitiesPath, $"{identity.Name}.json");
        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(metadataPath, json);
        SetFileSecurity(metadataPath);
    }

    public Task<bool> IdentityExistsAsync(string identityName)
    {
        var pfxPath = Path.Combine(_identitiesPath, $"{identityName}.pfx");
        return Task.FromResult(File.Exists(pfxPath));
    }

    private void SetFileSecurity(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var fileSecurity = new FileSecurity();

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
