namespace Percolator.PluginSdk.Profiles;

using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.PluginSdk;

/// <summary>
/// Contributed by plugins (e.g. Percolator.Apps.Chat) to generate an initial padded capability
/// payload (such as ProfileCapability) for outbound X3DH handshakes.
/// </summary>
public interface IInitialHandshakePayloadProvider
{
    ValueTask<(AppId AppId, ReadOnlyMemory<byte> Payload)?> CreateInitialPayloadAsync(
        PublicIdentityId localIdentityId,
        ChannelId channelId,
        CancellationToken ct = default);
}
