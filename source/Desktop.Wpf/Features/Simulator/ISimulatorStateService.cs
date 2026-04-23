using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateService
{
    IReadOnlyObservableList<SimulatedPeerModel> Peers { get; }

    IReadOnlyObservableList<SimulatedRelayModel> Relays { get; }

    IReadOnlyObservableList<PeerRelationship> Relationships { get; }

    Task<PeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(PeerId peerId, CancellationToken cancellationToken = default);

    Task<PeerId?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default);

    Task EnqueueRelayUpstreamToMainAsync(
        PeerId relayHostPeerId,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task EnqueueRelayDownstreamToPeerAsync(
        PeerId relayHostPeerId,
        byte[] targetPkh,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(
        PeerId relayHostPeerId,
        byte[] targetPkh,
        int max,
        CancellationToken cancellationToken = default);

    Task<int> ForwardRelayUpstreamToMainAsync(
        PeerId relayHostPeerId,
        SessionId relayHostToMainSessionId,
        int max,
        CancellationToken cancellationToken = default);

    Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(
        PeerId relayHostPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<bool> MoveRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default);

    Task<bool> CorruptRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task AddPublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default);
    Task RemovePublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default);

    Task AddRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default);
    Task RemoveRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default);

    Task<EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(
        PeerId simulatedPeerId,
        PeerId mainPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
        PeerId simulatedPeerId,
        PeerId inviterPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default);

    Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task ReceiveInviteHandshakeResponseFromMainAsync(
        PeerId simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task QueueInviteHandshakeResponseForDeliveryToMainAsync(
        PeerId simulatedPeerId,
        Guid requestCorrelationId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(
        PeerId simulatedPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);

    Task<SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(
        PeerId simulatedPeerId,
        PeerId acceptorPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        PeerId simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        PeerId simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default);

    Task PublishStandardPreKeyBundleToRelayAsync(
        PeerId simulatedPeerId,
        PeerId relayHostPeerId,
        DateTimeOffset expiresUtc,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default);

    Task<SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(
        PeerId simulatedPeerId,
        PeerId relayHostPeerId,
        byte[] responderPublicKeyHash,
        CancellationToken cancellationToken = default);

    Task<byte[]> ComputePublicKeyHashAsync(PeerId simulatedPeerId, CancellationToken cancellationToken = default);

    Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        PeerId simulatedPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<Plaintext> DecryptSessionMessageAsync(
        PeerId simulatedPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        PeerId simulatedPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default);

    Task UpsertPendingStandardSignalHelloAsync(
        PeerId recipientPeerId,
        PeerId relayHostPeerId,
        HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        PeerId recipientPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default);

    Task SendChatMessageToMainAsync(PeerId simulatedPeerId, string content, CancellationToken cancellationToken = default);
}