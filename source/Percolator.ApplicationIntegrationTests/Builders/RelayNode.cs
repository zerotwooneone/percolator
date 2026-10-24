using System.Collections.Concurrent;
using Percolator.Application2IntegrationTests.Adapters;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2IntegrationTests.Builders;

public sealed class RelayNode
{
    private readonly ConcurrentDictionary<PublicIdentityId, (BlindedRoutingToken RoutingToken, DeliveryToken DeliveryToken)> _registeredClientTokens = new();

    public string Name { get; }
    public EndpointPort Port { get; }
    public PublicIdentityId RelayIdentityId { get; }
    public RelayMailboxQueue MailboxQueue { get; }
    public InMemoryRelayPreKeyRepository PreKeyRepository { get; }
    public RelayHostingPolicy Policy { get; }
    public IDateTimeProvider TimeProvider { get; }

    public RelayNode(
        string name,
        EndpointPort port,
        RelayHostingPolicy? policy = null,
        IDateTimeProvider? timeProvider = null)
    {
        Name = name;
        Port = port;
        TimeProvider = timeProvider ?? new TestDateTimeProvider();
        Policy = policy ?? RelayHostingPolicy.Default;

        RelayIdentityId = PublicIdentityId.New();
        MailboxQueue = new RelayMailboxQueue(RelayIdentityId);
        PreKeyRepository = new InMemoryRelayPreKeyRepository();
    }

    public void RegisterClientMailbox(
        PublicIdentityId clientId,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken)
    {
        MailboxQueue.RegisterRecipient(routingToken, deliveryToken, drainToken);
        _registeredClientTokens[clientId] = (routingToken, deliveryToken);
    }

    public bool TryGetClientTokens(
        PublicIdentityId clientId,
        out (BlindedRoutingToken RoutingToken, DeliveryToken DeliveryToken) tokens)
    {
        return _registeredClientTokens.TryGetValue(clientId, out tokens);
    }
}
