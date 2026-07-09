using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using System.Net;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateService
{
    IReadOnlyObservableList<SimulatedPeerModel> Peers { get; }

    IReadOnlyObservableList<SimulatedRelayModel> Relays { get; }

    IReadOnlyObservableList<PeerRelationship> Relationships { get; }

    Task<Percolator.Network.NetworkPeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(Percolator.Network.NetworkPeerId networkPeerId, CancellationToken cancellationToken = default);

    Task<Percolator.Network.NetworkPeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(IdentityPublicKeyHash recipientPublicKeyHash, CancellationToken cancellationToken = default);

    Task EnqueueRelayUpstreamToMainAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task EnqueueRelayDownstreamToPeerAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
        int max,
        CancellationToken cancellationToken = default);

    Task<int> ForwardRelayUpstreamToMainAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        SessionId relayHostToMainSessionId,
        int max,
        CancellationToken cancellationToken = default);

    Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRelayMessageByAckIdAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<bool> MoveRelayMessageByAckIdAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default);

    Task<bool> CorruptRelayMessageByAckIdAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task AddPublishedKeysRelationshipAsync(Percolator.Network.NetworkPeerId publisherNetworkPeerId, Percolator.Network.NetworkPeerId hostNetworkPeerId, CancellationToken cancellationToken = default);
    Task RemovePublishedKeysRelationshipAsync(Percolator.Network.NetworkPeerId publisherNetworkPeerId, Percolator.Network.NetworkPeerId hostNetworkPeerId, CancellationToken cancellationToken = default);

    Task AddRelayActiveSessionAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Percolator.Network.NetworkPeerId networkPeerId, CancellationToken cancellationToken = default);
    Task RemoveRelayActiveSessionAsync(Percolator.Network.NetworkPeerId relayHostNetworkPeerId, Percolator.Network.NetworkPeerId networkPeerId, CancellationToken cancellationToken = default);

    Task<EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId mainNetworkPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default);

    Task AcceptPendingInboundDirectInviteAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task RejectPendingInboundDirectInviteAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Guid correlationId,
        CancellationToken cancellationToken = default);

    Task<SimulatedPeerInviteAcceptance> AcceptInboundDirectInviteAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId inviterNetworkPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default);

    Task HandleInboundInviteHandshakeResponseFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default);

    Task PublishStandardPreKeyBundleToRelayAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        DateTimeOffset expiresUtc,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default);

    Task<SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Identity.IdentityPublicKeyHash responderPublicKeyHash,
        CancellationToken cancellationToken = default);

    Task<byte[]> ComputePublicKeyHashAsync(Percolator.Network.NetworkPeerId simulatedNetworkPeerId, CancellationToken cancellationToken = default);

    Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<Plaintext> DecryptSessionMessageAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Percolator.Network.NetworkPeerId simulatedNetworkPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default);

    Task UpsertPendingStandardSignalHelloAsync(
        Percolator.Network.NetworkPeerId recipientNetworkPeerId,
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        Percolator.Network.NetworkPeerId recipientNetworkPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default);

    Task SendChatMessageToMainAsync(Percolator.Network.NetworkPeerId simulatedNetworkPeerId, string content, CancellationToken cancellationToken = default);

    bool TryResolvePeerId(DnsEndPoint endpoint, out Percolator.Network.NetworkPeerId? networkPeerId);
}