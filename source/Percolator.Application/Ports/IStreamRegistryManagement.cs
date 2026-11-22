using System;
using System.Threading.Channels;
using Percolator.Domain.Identities;
using Percolator.PluginSdk;

namespace Percolator.Application.Ports;

public interface IStreamRegistryManagement : IStreamRegistry, IDirectEndpointRegistry
{
    ChannelReader<ReadOnlyMemory<byte>> RegisterStream(PublicIdentityId targetId, int? capacity = null);
    ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(PublicIdentityId relayId, int? capacity = null);
    void UnregisterStream(PublicIdentityId targetId);
    void UnregisterRelayStream(PublicIdentityId relayId);
}
