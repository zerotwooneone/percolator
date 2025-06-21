using Google.Protobuf;
using Percolator.Contracts.Protos;

namespace Percolator.Application;

public interface IManifestService
{
    (ByteString hash, SignedManifest manifest) CreateManifestFromFile(string path);
}
