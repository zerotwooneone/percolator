using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Common;
using Percolator.PluginSdk;
using Percolator.SourceGenerators;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public enum DirectCandidateKind : byte
{
    DirectLan = 0x01,
    DirectWanUpnp = 0x02,
    DirectWanStun = 0x03
}

public enum DirectWanPolicy : byte
{
    DisabledByDefault = 0x01,
    ExplicitlyAllowed = 0x02
}

public enum TransportRoutingPreference : byte
{
    DirectPreferred = 0x01,
    RelayOnly = 0x02
}

public sealed record EndpointHost
{
    public string Value { get; }

    private EndpointHost(string value) => Value = value;

    public static EndpointHost Parse(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be empty.", nameof(host));
        return new EndpointHost(host.Trim());
    }
}

public readonly record struct PortNumber
{
    public ushort Value { get; }

    private PortNumber(ushort value) => Value = value;

    public static PortNumber FromInt(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1..65535.");
        return new PortNumber((ushort)port);
    }
}

public sealed record DirectEndpointCandidate(
    EndpointHost Host,
    PortNumber Port,
    DirectCandidateKind Kind);

public sealed record RelayPipeEndpointUri
{
    public string Value { get; }

    private RelayPipeEndpointUri(string value) => Value = value;

    public static RelayPipeEndpointUri Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("Relay pipe URI cannot be empty.", nameof(uri));
        return new RelayPipeEndpointUri(uri.Trim());
    }
}

[ByteArray(length: 32)]
public sealed partial record RelayPipeToken
{
    public static RelayPipeToken Generate() => FromBytes(RandomNumberGenerator.GetBytes(32));
}

public sealed record RelayRendezvousDescriptor(
    RelayPipeEndpointUri EndpointUri,
    RelayPipeToken Token);

[ByteArray(length: 32)]
public sealed partial record TransferKey : ISensitiveSecret
{
    public static TransferKey Generate() => FromBytes(RandomNumberGenerator.GetBytes(32));

    public void Zeroize()
    {
        if (MemoryMarshal.TryGetArray(Memory, out var segment) && segment.Array != null)
        {
            CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }

    public void Dispose()
    {
        Zeroize();
    }
}

[ByteArray(length: 12)]
public sealed partial record BaseNonce
{
    public static BaseNonce Generate() => FromBytes(RandomNumberGenerator.GetBytes(12));
}

[ByteArray(length: 32)]
public sealed partial record StreamAuthToken
{
    public static StreamAuthToken Generate() => FromBytes(RandomNumberGenerator.GetBytes(32));
}

[ByteArray(length: 32)]
public sealed partial record SwarmRendezvousId
{
    public static SwarmRendezvousId Derive(TransferKey key)
    {
        byte[] derived = HMACSHA256.HashData(key.Span, System.Text.Encoding.UTF8.GetBytes("swarm-rendezvous"));
        return FromBytes(derived);
    }
}

public readonly record struct StreamIndex(int Value)
{
    public static StreamIndex FromInt(int index) => new(index);
}

public readonly record struct StreamCount(int Count)
{
    public static StreamCount FromInt(int count) => new(count);
    public static StreamCount Default => new(4);
}

public readonly record struct TransferBandwidth(long BytesPerSecond)
{
    public static TransferBandwidth FromBytesPerSecond(long bytesPerSecond) => new(bytesPerSecond);
}

public readonly record struct BandwidthLimit(bool IsUnlimited, TransferBandwidth Value)
{
    public static BandwidthLimit Unlimited => new(true, default);
    public static BandwidthLimit Capped(TransferBandwidth cap) => new(false, cap);
}

public sealed record RootDirectoryPath
{
    public string Value { get; }

    private RootDirectoryPath(string value) => Value = value;

    public static RootDirectoryPath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        return new RootDirectoryPath(Path.GetFullPath(path));
    }
}

public sealed record ResolvedFilePath
{
    public string AbsolutePath { get; }

    private ResolvedFilePath(string absolutePath) => AbsolutePath = absolutePath;

    public static ResolvedFilePath Resolve(RootDirectoryPath root, SanitizedRelativePath relativePath) =>
        new(Path.Combine(root.Value, relativePath.Value));
}

public sealed record EncryptedChunkData
{
    public ReadOnlyMemory<byte> CiphertextWithAuthTag { get; }
    public int Length => CiphertextWithAuthTag.Length;
    public ReadOnlySpan<byte> Span => CiphertextWithAuthTag.Span;

    private EncryptedChunkData(ReadOnlyMemory<byte> data) => CiphertextWithAuthTag = data;
    public static EncryptedChunkData FromBytes(byte[] data) => new(data ?? throw new ArgumentNullException(nameof(data)));
    public static EncryptedChunkData FromMemory(ReadOnlyMemory<byte> memory) => new(memory);
}

public sealed record InlineManifestData
{
    public ReadOnlyMemory<byte> SerializedBytes { get; }
    public int Length => SerializedBytes.Length;
    public ReadOnlySpan<byte> Span => SerializedBytes.Span;

    private InlineManifestData(ReadOnlyMemory<byte> bytes) => SerializedBytes = bytes;
    public static InlineManifestData FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 49_152)
            throw new ArgumentException("Inline manifest must be between 1 and 49152 bytes.", nameof(bytes));
        return new InlineManifestData(bytes);
    }

    public static InlineManifestData FromSpan(ReadOnlySpan<byte> bytes) => FromBytes(bytes.ToArray());

    public byte[] ToByteArray() => SerializedBytes.ToArray();
}

public sealed record ManifestBlobDescriptor(
    BlobId BlobId,
    ChunkHash CiphertextSha256,
    TotalBundleBytes PlaintextSizeBytes,
    RelayPipeEndpointUri RelayEndpointUri);

public enum ManifestDistributionKind : byte
{
    Inline = 0x01,
    RemoteBlob = 0x02
}

public sealed record ManifestDistribution
{
    public ManifestDistributionKind Kind { get; }
    public InlineManifestData? InlineData { get; }
    public ManifestBlobDescriptor? BlobDescriptor { get; }

    private ManifestDistribution(ManifestDistributionKind kind, InlineManifestData? inlineData, ManifestBlobDescriptor? blob)
    {
        Kind = kind;
        InlineData = inlineData;
        BlobDescriptor = blob;
    }

    public static ManifestDistribution FromInline(InlineManifestData data) =>
        new(ManifestDistributionKind.Inline, data ?? throw new ArgumentNullException(nameof(data)), null);

    public static ManifestDistribution FromBlob(ManifestBlobDescriptor blob) =>
        new(ManifestDistributionKind.RemoteBlob, null, blob ?? throw new ArgumentNullException(nameof(blob)));
}

public sealed record DiskSpacePreflightResult(
    bool HasSufficientSpace,
    TotalBundleBytes RequiredBytes,
    TotalBundleBytes AvailableBytes)
{
    public static DiskSpacePreflightResult Sufficient() =>
        new(true, TotalBundleBytes.Zero, TotalBundleBytes.FromBytes(long.MaxValue));
    public static DiskSpacePreflightResult Sufficient(TotalBundleBytes required, TotalBundleBytes available) =>
        new(true, required, available);
    public static DiskSpacePreflightResult Insufficient(long shortfallBytes) =>
        new(false, TotalBundleBytes.FromBytes(shortfallBytes), TotalBundleBytes.Zero);
    public static DiskSpacePreflightResult Insufficient(TotalBundleBytes required, TotalBundleBytes available) =>
        new(false, required, available);
}
