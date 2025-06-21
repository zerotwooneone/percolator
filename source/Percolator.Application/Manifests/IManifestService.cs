using Google.Protobuf;
using Percolator.Contracts.Protos;

namespace Percolator.Application.Manifests;

public interface IManifestService
{
    IEnumerable<(ByteString hash, SignedManifest manifest)> CreateManifestsFromSharedDirectories();
}
