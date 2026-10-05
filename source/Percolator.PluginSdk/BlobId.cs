namespace Percolator.PluginSdk;

public readonly record struct BlobId(string HexDigest)
{
    public static BlobId FromSha256(byte[] sha256Bytes)
    {
        ArgumentNullException.ThrowIfNull(sha256Bytes);
        return new BlobId(Convert.ToHexString(sha256Bytes).ToLowerInvariant());
    }

    public override string ToString() => HexDigest;
}
