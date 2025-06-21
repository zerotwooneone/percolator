using Google.Protobuf;
using Percolator.Contracts.Protos;

namespace Percolator.Application
{
    public static class ManifestUtils
    {
        public static byte[] GetCanonicalBytes(Manifest manifest)
        {
            var canonicalManifest = manifest.Clone();
            // Sort the entries by path to ensure a deterministic byte representation for signing
            var sortedEntries = canonicalManifest.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            canonicalManifest.Entries.Clear();
            canonicalManifest.Entries.AddRange(sortedEntries);
            return canonicalManifest.ToByteArray();
        }
    }
}
