using Google.Protobuf;
using Percolator.Contracts.Protos;

namespace Percolator.Application;

public interface IManifestStore
{
    void Add(ByteString hash, SignedManifest manifest, string? rootPath = null);
    SignedManifest? Get(ByteString hash);
}
