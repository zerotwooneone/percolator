using System;
using System.Security.Cryptography;
using System.Text;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Channels.ValueObjects;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct ChannelId
{
    public static ChannelId DeterministicForDirect(PublicIdentityId a, PublicIdentityId b)
    {
        var (first, second) = a.Value.CompareTo(b.Value) < 0 ? (a, b) : (b, a);
        Span<byte> combined = stackalloc byte[32];
        first.TryWriteBytes(combined[..16]);
        second.TryWriteBytes(combined[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(combined, hash);
        return FromBytes(hash[..16]);
    }

    public static ChannelId DeterministicForDirect(PublicIdentityId a, PublicIdentityId b, RelayEndpoint relayEndpoint)
    {
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        var (first, second) = a.Value.CompareTo(b.Value) < 0 ? (a, b) : (b, a);
        var endpointBytes = Encoding.UTF8.GetBytes(relayEndpoint.Value);
        Span<byte> combined = stackalloc byte[32 + endpointBytes.Length];
        first.TryWriteBytes(combined[..16]);
        second.TryWriteBytes(combined[16..32]);
        endpointBytes.CopyTo(combined[32..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(combined, hash);
        return FromBytes(hash[..16]);
    }
}
