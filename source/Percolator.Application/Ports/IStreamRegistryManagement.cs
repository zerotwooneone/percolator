using System;
using System.Threading.Channels;
using Percolator.Domain.Identities;

namespace Percolator.Application.Ports;

public interface IStreamRegistryManagement : IStreamRegistry
{
    ChannelReader<ReadOnlyMemory<byte>> RegisterStream(PublicIdentityId targetId, int? capacity = null);
    ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(PublicIdentityId relayId, int? capacity = null);
    void UnregisterStream(PublicIdentityId targetId);
    void UnregisterRelayStream(PublicIdentityId relayId);
    void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint);
    void UnregisterDirectEndpoint(PublicIdentityId targetId);
    Uri? GetDirectEndpoint(PublicIdentityId targetId);
}
