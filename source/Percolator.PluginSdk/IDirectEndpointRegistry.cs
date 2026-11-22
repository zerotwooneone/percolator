namespace Percolator.PluginSdk;

using System;
using Percolator.Domain.Identities;

/// <summary>
/// Port for registering and querying direct peer endpoints in the transport layer.
/// </summary>
public interface IDirectEndpointRegistry
{
    void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint);
    void UnregisterDirectEndpoint(PublicIdentityId targetId);
    Uri? GetDirectEndpoint(PublicIdentityId targetId);
}
