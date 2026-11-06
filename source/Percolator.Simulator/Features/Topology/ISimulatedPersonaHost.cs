using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Simulator.Features.RelayedChat;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal interface ISimulatedPersonaHost : IAsyncDisposable
{
    SimulatedNodeId Id { get; }
    string DisplayName { get; }
    int BoundPort { get; }
    Uri BoundEndpoint { get; }
    byte[] IdenticonPng { get; }
    IServiceProvider Services { get; }

    PublicIdentityId PublicIdentityId { get; }
    IdentityKey IdentityPublicKey { get; }
    DhPublicKey SignedPreKeyPublic { get; }
    DeviceLinkProof SignedPreKeySignature { get; }

    EphemeralPrivateKey IdentityPrivateKey { get; }
    BlindedRoutingToken MailboxRoutingToken { get; }
    DeliveryToken MailboxDeliveryToken { get; }
    DeliveryToken MailboxDrainToken { get; }
    ISimulatedRelayHost? HomeRelay { get; set; }

    ReadOnlyReactiveProperty<bool> IsListening { get; }
    void SetListening(bool isListening);

    PreKeyBundle CreatePreKeyBundle(uint? oneTimeKeyId = null);
    Task<DomainResult> FlushOutboxAsync(CancellationToken ct = default);
    Task<int> DrainMailboxAsync(CancellationToken ct = default);

    ValueTask StartAsync(CancellationToken ct = default);
    ValueTask StopAsync(CancellationToken ct = default);
}
