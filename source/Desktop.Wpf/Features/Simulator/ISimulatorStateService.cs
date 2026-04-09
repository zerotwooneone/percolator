using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateService
{
    IReadOnlyObservableList<SimulatedPeerModel> Peers { get; }

    IReadOnlyObservableList<SimulatedRelayModel> Relays { get; }

    IReadOnlyObservableList<PeerRelationship> Relationships { get; }

    Task<Guid> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default);
    
    Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default);

    Task EnqueueRelayUpstreamToMainAsync(
        Guid relayHostPeerId,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task EnqueueRelayDownstreamToPeerAsync(
        Guid relayHostPeerId,
        byte[] targetPkh,
        byte[] opaqueBytes,
        string? debugType = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(
        Guid relayHostPeerId,
        byte[] targetPkh,
        int max,
        CancellationToken cancellationToken = default);

    Task<int> ForwardRelayUpstreamToMainAsync(
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        int max,
        CancellationToken cancellationToken = default);

    Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(
        Guid relayHostPeerId,
        SessionId relayHostToMainSessionId,
        Guid ackId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task<bool> MoveRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default);

    Task<bool> CorruptRelayMessageByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default);

    Task AddPublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default);
    Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default);

    Task AddRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default);
    Task RemoveRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default);

    Task<EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(
        Guid simulatedPeerId,
        Guid inviterPeerId,
        EstablishDirectSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<SimulatedPeerInviteAcceptance> AcceptReverseSignalInviteAsync(
        Guid simulatedPeerId,
        Guid inviterPeerId,
        EstablishDirectSessionRequest invite,
        CancellationToken cancellationToken = default);

    Task DeliverInviteHandshakeResponseToMainAsync(
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task ReceiveInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task QueueInviteHandshakeResponseForDeliveryToMainAsync(
        Guid simulatedPeerId,
        Guid requestCorrelationId,
        InviteHandshakeResponse response,
        CancellationToken cancellationToken = default);

    Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(
        Guid simulatedPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);

    Task<SessionId?> TryFinalizeInviteHandshakeResponseFromMainAsync(
        Guid simulatedPeerId,
        Guid acceptorPeerId,
        Guid requestCorrelationId,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(
        Guid simulatedPeerId,
        EstablishSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(
        Guid simulatedPeerId,
        DeliverOpaqueMessageRequest request,
        CancellationToken cancellationToken = default);

    Task PublishStandardPreKeyBundleToRelayAsync(
        Guid simulatedPeerId,
        Guid relayHostPeerId,
        DateTimeOffset expiresUtc,
        bool includeOneTimeKeys,
        int oneTimeKeyCount,
        CancellationToken cancellationToken = default);

    Task<SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(
        Guid simulatedPeerId,
        Guid relayHostPeerId,
        byte[] responderPublicKeyHash,
        CancellationToken cancellationToken = default);

    Task<byte[]> ComputePublicKeyHashAsync(Guid simulatedPeerId, CancellationToken cancellationToken = default);

    Task<SessionRatchetMessage> EncryptInternalEnvelopeAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        InternalEnvelope envelope,
        CancellationToken cancellationToken = default);

    Task<Plaintext> DecryptSessionMessageAsync(
        Guid simulatedPeerId,
        SessionId sessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default);

    Task<EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(
        Guid simulatedPeerId,
        byte[] opaqueBytes,
        CancellationToken cancellationToken = default);

    Task UpsertPendingStandardSignalHelloAsync(
        Guid recipientPeerId,
        Guid relayHostPeerId,
        HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        Guid recipientPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default);
}