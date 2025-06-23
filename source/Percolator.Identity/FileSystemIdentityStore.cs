using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity.Model;

namespace Percolator.Identity;

public class FileSystemIdentityStore : IIdentityStore
{
    private readonly string _identitiesPath;
    private record IdentityMetadata(string? Nickname, string Thumbprint);

    public FileSystemIdentityStore()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
        _identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
        Directory.CreateDirectory(_identitiesPath);
    }

    public async Task<IdentityRecord?> GetIdentityAsync(string identityName, CancellationToken cancellationToken = default)
    {
        var pfxPath = Path.Combine(_identitiesPath, $"{identityName}.pfx");
        if (!File.Exists(pfxPath))
        {
            return null;
        }

        var pfxBytes = await File.ReadAllBytesAsync(pfxPath, cancellationToken);
        var pfxCertificate = new PfxCertificate(pfxBytes);

        var metadataPath = Path.Combine(_identitiesPath, $"{identityName}.json");
        if (!File.Exists(metadataPath))
        {
            throw new InvalidOperationException($"Identity '{identityName}' is missing its metadata file.");
        }

        var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
        var metadata = JsonSerializer.Deserialize<IdentityMetadata>(json);
        if (metadata is null || string.IsNullOrEmpty(metadata.Thumbprint))
        {
            throw new InvalidOperationException($"Identity '{identityName}' is missing its thumbprint in metadata.");
        }

        return new IdentityRecord(identityName, pfxCertificate, metadata.Thumbprint, metadata.Nickname);
    }

    public Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = Directory.EnumerateFiles(_identitiesPath, "*.pfx")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!);
        return Task.FromResult(names);
    }

    public async Task StoreIdentityAsync(IdentityRecord identity, CancellationToken cancellationToken = default)
    {
        var pfxPath = Path.Combine(_identitiesPath, $"{identity.Name}.pfx");
        await File.WriteAllBytesAsync(pfxPath, identity.PfxCertificate.Value, cancellationToken);
        SetFileSecurity(pfxPath);

        var metadata = new IdentityMetadata(identity.Nickname, identity.Thumbprint);
        var metadataPath = Path.Combine(_identitiesPath, $"{identity.Name}.json");
        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(metadataPath, json, cancellationToken);
        SetFileSecurity(metadataPath);
    }

    public Task<bool> IdentityExistsAsync(string identityName, CancellationToken cancellationToken = default)
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
