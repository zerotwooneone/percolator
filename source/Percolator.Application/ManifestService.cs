using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Contracts.Protos;
using Percolator.Identity;
using Percolator.Cryptography;

namespace Percolator.Application;

public class ManifestService : IManifestService
{
    private readonly IIdentityService _identityService;
    private readonly IManifestStore _manifestStore;
    private readonly ISignatureService _signatureService;

    public ManifestService(IIdentityService identityService, IManifestStore manifestStore, ISignatureService signatureService)
    { 
        _identityService = identityService;
        _manifestStore = manifestStore;
        _signatureService = signatureService;
    }

    public (ByteString hash, SignedManifest manifest) CreateManifestFromFile(string path)
    {
        var manifest = new Manifest();
        var basePath = Path.GetDirectoryName(path) ?? string.Empty;
        PopulateManifestEntries(manifest, path, basePath);

        var manifestBytes = manifest.ToByteArray();

        using var sha256 = SHA256.Create();
        var hash = ByteString.CopyFrom(sha256.ComputeHash(manifestBytes));

        var signedManifest = new SignedManifest
        {
            Version = 1,
            Manifest = manifest
        };
        _signatureService.Sign(signedManifest);

        _manifestStore.Add(hash, signedManifest, path);

        return (hash, signedManifest);
    }

    private void PopulateManifestEntries(Manifest manifest, string currentPath, string basePath)
    {
        var attributes = File.GetAttributes(currentPath);
        var relativePath = Path.GetRelativePath(basePath, currentPath);

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            manifest.Entries.Add(new ManifestEntry
            {
                Type = ManifestEntry.Types.ManifestEntryType.Directory,
                Path = relativePath
            });

            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                PopulateManifestEntries(manifest, dir, basePath);
            }
            foreach (var file in Directory.GetFiles(currentPath))
            {
                PopulateManifestEntries(manifest, file, basePath);
            }
        }
        else
        {
            var fileInfo = new FileInfo(currentPath);
            using var sha256 = SHA256.Create();
            using var fileStream = File.OpenRead(currentPath);
            var fileHash = sha256.ComputeHash(fileStream);

            manifest.Entries.Add(new ManifestEntry
            {
                Type = ManifestEntry.Types.ManifestEntryType.File,
                Path = relativePath,
                Size = fileInfo.Length,
                Hash = ByteString.CopyFrom(fileHash)
            });
        }
    }
}

public abstract class FileSystemNode
{
    public string Name { get; set; } = string.Empty;
}

public class FileNode : FileSystemNode
{
    public long Size { get; set; }
}

public class DirectoryNode : FileSystemNode
{
    public List<FileSystemNode> Children { get; set; } = new();
}
