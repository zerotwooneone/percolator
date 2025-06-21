using System.Collections.Concurrent;
using Google.Protobuf;
using Percolator.Contracts.Protos;
using System.Collections.Generic;

namespace Percolator.Application
{
    public class ManifestStore
    {
        private readonly ConcurrentDictionary<ByteString, SignedManifest> _manifests = new();

        public void StoreManifest(ByteString hash, SignedManifest manifest)
        {
            _manifests.TryAdd(hash, manifest);
        }

        public SignedManifest? GetManifest(ByteString hash)
        {
            _manifests.TryGetValue(hash, out var manifest);
            return manifest;
        }

        public IEnumerable<ByteString> GetManifestHashes()
        {
            return _manifests.Keys;
        }
    }
}
