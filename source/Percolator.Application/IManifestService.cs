using Google.Protobuf;
using Percolator.Contracts.Protos;
using System.Collections.Generic;

namespace Percolator.Application;

public interface IManifestService
{
    IEnumerable<(ByteString hash, SignedManifest manifest)> CreateManifestsFromSharedDirectories();
}
