using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateService
{
    IReadOnlyObservableList<SimulatedPeerModel> Peers { get; }

    IReadOnlyObservableList<SimulatedRelayModel> Relays { get; }

    IReadOnlyObservableList<PeerRelationship> Relationships { get; }

    Task<Percolator.Network.PeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(Percolator.Network.PeerId peerId, CancellationToken cancellationToken = default);

    Task<Percolator.Network.PeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(IdentityPublicKeyHash recipientPublicKeyHash, CancellationToken cancellationToken = default);

    Task EnqueueRelayUpstreamToMainAsync(
        Percolator.Network.PeerId relayHostPeerId,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task EnqueueRelayDownstreamToPeerAsync(
        Percolator.Network.PeerId relayHostPeerId,
        Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(
        Percolator.Network.PeerId relayHostPeerId,
        byte[] targetIdentityPublicKeyHash,
        int max,
        CancellationToken cancellationToken = default);

    Task<int> ForwardRelayUpstreamToMainAsync(
        Percolator.Network.PeerId relayHostPeerId,
        SessionId relayHostToMainSessionId,
        int max,
        CancellationToken cancellationToken = default);

    Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(
        Percolator.Network.PeerId relayHostPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRelayMessageByAckIdAsync(Percolator.Network.PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<bool> MoveRelayMessageByAckIdAsync(Percolator.Network.PeerId relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default);

    Task<bool> CorruptRelayMessageByAckIdAsync(Percolator.Network.PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task AddPublishedKeysRelationshipAsync(Percolator.Network.PeerId publisherPeerId, Percolator.Network.PeerId hostPeerId, CancellationToken cancellationToken = default);
    Task RemovePublishedKeysRelationshipAsync(Percolator.Network.PeerId publisherPeerId, Percolator.Network.PeerId hostPeerId, CancellationToken cancellationToken = default);

    Task AddRelayActiveSessionAsync(Percolator.Network.PeerId relayHostPeerId, Percolator.Network.PeerId peerId, CancellationToken cancellationToken = default);
    Task RemoveRelayActiveSessionAsync(Percolator.Network.PeerId relayHostPeerId, Percolator.Network.PeerId peerId, CancellationToken cancellationToken = default);

    Task<EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Percolator.Network.PeerId mainPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Percolator.Network.PeerId inviterPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default);

    Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task ReceiveInviteHandshakeResponseFromMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task QueueInviteHandshakeResponseForDeliveryToMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Guid requestCorrelationId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);

    Task<SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Percolator.Network.PeerId acceptorPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Percolator.Network.PeerId simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default);

    Task PublishStandardPreKeyBundleToRelayAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Percolator.Network.PeerId relayHostPeerId,
        DateTimeOffset expiresUtc,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default);

    Task<SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(
        Percolator.Network.PeerId simulatedPeerId,
        Percolator.Network.PeerId relayHostPeerId,
        byte[] responderPublicKeyHash,
        CancellationToken cancellationToken = default);

    Task<byte[]> ComputePublicKeyHashAsync(Percolator.Network.PeerId simulatedPeerId, CancellationToken cancellationToken = default);

    Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Percolator.Network.PeerId simulatedPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<Plaintext> DecryptSessionMessageAsync(
        Percolator.Network.PeerId simulatedPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Percolator.Network.PeerId simulatedPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default);

    Task UpsertPendingStandardSignalHelloAsync(
        Percolator.Network.PeerId recipientPeerId,
        Percolator.Network.PeerId relayHostPeerId,
        HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        Percolator.Network.PeerId recipientPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default);

    Task SendChatMessageToMainAsync(Percolator.Network.PeerId simulatedPeerId, string content, CancellationToken cancellationToken = default);
}