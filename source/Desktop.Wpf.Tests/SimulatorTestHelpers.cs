using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using ObservableCollections;
using Percolator.Identity;
using PeerId = Percolator.Network.PeerId;

namespace Desktop.Wpf.Tests;

public sealed class StateStub : TestSimulatorStateServiceBase
{
}

public abstract class TestSimulatorStateServiceBase : ISimulatorStateService
{
    private readonly ObservableList<SimulatedPeerModel> _peers = new();
    private readonly ObservableList<SimulatedRelayModel> _relays = new();
    private readonly ObservableList<PeerRelationship> _relationships = new();

    public IReadOnlyObservableList<SimulatedPeerModel> Peers => _peers;
    public IReadOnlyObservableList<SimulatedRelayModel> Relays => _relays;
    public IReadOnlyObservableList<PeerRelationship> Relationships => _relationships;

    public PeerId? ResolvePkhToPeerId { get; set; }

    public virtual void AddRelay(PeerId relayHostPeerId, bool autoDeliverEnabled)
    {
        var relay = new SimulatedRelayModel(relayHostPeerId);
        relay.AutoDeliverEnabled.Value = autoDeliverEnabled;
        _relays.Add(relay);
    }

    public virtual void AddRelay(SimulatedRelayModel relay) => _relays.Add(relay);

    public virtual Task<PeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(IdentityPublicKeyHash recipientPublicKeyHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResolvePkhToPeerId);
    }

    public virtual Task<bool> DeleteRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relay = _relays.FirstOrDefault(r => r.RelayHostPeerId.Value == relayHostPeerId.Value);
        if (relay is null) return Task.FromResult(false);
        var removed = relay.RemoveMessage(ackId);
        return Task.FromResult(removed);
    }

    public virtual Task<bool> DeliverRelayUpstreamToMainByAckIdAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, Guid ackId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PeerId> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RemovePeerAsync(PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task EnqueueRelayUpstreamToMainAsync(PeerId relayHostPeerId, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task EnqueueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<InboundRelayMessage>> DequeueRelayDownstreamToPeerAsync(PeerId relayHostPeerId, Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<int> ForwardRelayUpstreamToMainAsync(PeerId relayHostPeerId, Percolator.Cryptography.SessionId relayHostToMainSessionId, int max, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> MoveRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> CorruptRelayMessageByAckIdAsync(PeerId relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddPublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RemovePublishedKeysRelationshipAsync(PeerId publisherPeerId, PeerId hostPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AddRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RemoveRelayActiveSessionAsync(PeerId relayHostPeerId, PeerId peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.EstablishDirectSessionResponse> ReceiveEstablishDirectSessionFromMainAsync(PeerId simulatedPeerId, PeerId mainPeerId, Percolator.Contracts.EstablishDirectSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task AcceptPendingInboundDirectInviteAsync(PeerId simulatedPeerId, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task RejectPendingInboundDirectInviteAsync(PeerId simulatedPeerId, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<SimulatedPeerInviteAcceptance> AcceptInboundDirectInviteAsync(PeerId simulatedPeerId, PeerId inviterPeerId, Percolator.Contracts.EstablishDirectSessionRequest invite, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeliverInviteHandshakeResponseToMainAsync(Percolator.Contracts.InviteHandshakeResponse response, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task HandleInboundInviteHandshakeResponseFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.InviteHandshakeResponse response, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task QueueInviteHandshakeResponseForDeliveryToMainAsync(PeerId simulatedPeerId, Guid requestCorrelationId, Percolator.Contracts.InviteHandshakeResponse response, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<bool> TryDeliverQueuedInviteHandshakeResponseToMainAsync(PeerId simulatedPeerId, Guid requestCorrelationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.EstablishSessionResponse> ReceiveEstablishSessionFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.EstablishSessionRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.DeliverOpaqueMessageResponse> ReceiveOpaqueMessageFromMainAsync(PeerId simulatedPeerId, Percolator.Contracts.DeliverOpaqueMessageRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task PublishStandardPreKeyBundleToRelayAsync(PeerId simulatedPeerId, PeerId relayHostPeerId, DateTimeOffset expiresUtc, int oneTimeKeyCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.SessionId?> InitiateStandardHandshakeToMainByRelayPkhAsync(PeerId simulatedPeerId, PeerId relayHostPeerId, Percolator.Identity.IdentityPublicKeyHash responderPublicKeyHash, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<byte[]> ComputePublicKeyHashAsync(PeerId simulatedPeerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.SessionRatchetMessage> EncryptInternalEnvelopeAsync(PeerId simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Contracts.InternalEnvelope envelope, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Cryptography.Plaintext> DecryptSessionMessageAsync(PeerId simulatedPeerId, Percolator.Cryptography.SessionId sessionId, Percolator.Cryptography.SessionRatchetMessage message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<Percolator.Contracts.EstablishSessionResponse?> ReceiveRelayedOpaquePayloadAsync(PeerId simulatedPeerId, byte[] opaqueBytes, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public virtual Task UpsertPendingStandardSignalHelloAsync(
        PeerId recipientPeerId,
        PeerId relayHostPeerId,
        Percolator.Contracts.HandshakeInitiatorHello hello,
        DateTimeOffset receivedUtc,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual Task<bool> TryAcceptPendingStandardSignalHelloAsync(
        PeerId recipientPeerId,
        string initiatorPkhHex,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public virtual Task SendChatMessageToMainAsync(PeerId simulatedPeerId, string content, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public virtual bool TryResolvePeerId(DnsEndPoint endpoint, out PeerId peerId)
    {
        peerId = default;
        return false;
    }
}

/// <summary>
/// Shared test double for ISimulatorStateRepository used across simulator tests.
/// Stores a single SimulatorStateSnapshot in-memory and tracks load/save calls.
/// </summary>
internal sealed class InMemorySimulatorStateRepository : ISimulatorStateRepository
{
    private SimulatorStateSnapshot? _snapshot;

    /// <summary>
    /// The most recently saved snapshot (exact reference/value saved).
    /// </summary>
    public SimulatorStateSnapshot? LastSavedSnapshot { get; private set; }

    /// <summary>
    /// Number of times SaveStateAsync was called.
    /// </summary>
    public int SaveCallCount { get; private set; }

    /// <summary>
    /// Number of times LoadStateAsync was called.
    /// </summary>
    public int LoadCallCount;

    /// <summary>
    /// The current snapshot (never null; returns empty snapshot if not seeded).
    /// </summary>
    public SimulatorStateSnapshot Current
    {
        get
        {
            return _snapshot ?? new SimulatorStateSnapshot(
                Version: 1,
                Peers: Array.Empty<PeerStateSnapshot>(),
                Relationships: Array.Empty<PeerRelationshipSnapshot>(),
                Relays: Array.Empty<RelayStateSnapshot>(),
                Groups: Array.Empty<GroupConversationDto>());
        }
    }

    /// <summary>
    /// Seeds the repository with a specific snapshot.
    /// </summary>
    public void Seed(SimulatorStateSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref LoadCallCount);
        return Task.FromResult(Current);
    }

    public Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        SaveCallCount++;
        LastSavedSnapshot = snapshot;
        _snapshot = snapshot;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Thin wrapper around InMemorySimulatorStateRepository that gates LoadStateAsync
/// with a TaskCompletionSource, used for initialization blocking tests.
/// </summary>
internal sealed class GatedLoadSimulatorStateRepository : ISimulatorStateRepository
{
    private readonly TaskCompletionSource<SimulatorStateSnapshot> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InMemorySimulatorStateRepository _inner;

    public GatedLoadSimulatorStateRepository(InMemorySimulatorStateRepository inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// Releases the load gate, allowing LoadStateAsync to proceed.
    /// The inner repository must be seeded with the desired snapshot before calling this.
    /// </summary>
    public void ReleaseLoad()
    {
        _gate.TrySetResult(_inner.Current);
    }

    /// <summary>
    /// Releases the load gate with a specific snapshot, seeding the inner repository as well.
    /// </summary>
    public void ReleaseLoadWith(SimulatorStateSnapshot snapshot)
    {
        _inner.Seed(snapshot);
        _gate.TrySetResult(snapshot);
    }

    public Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _inner.LoadCallCount);
        return _gate.Task;
    }

    public Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        return _inner.SaveStateAsync(snapshot, cancellationToken);
    }
}

/// <summary>
/// Helper methods for creating cryptographic test data.
/// </summary>
internal static class CryptoTestHelpers
{
    /// <summary>
    /// Creates a complete peer model with cryptographic keys for testing.
    /// </summary>
    public static SimulatedPeerModel CreateTestPeer(
        Percolator.Network.PeerId peerId,
        int selfIdentityId,
        string displayName,
        bool isRelayCapable,
        System.Net.DnsEndPoint endpoint)
    {
        using var identityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var identityPriv = identityEcdh.ExportECPrivateKey();
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));
        var identitySpki = identityEcdsa.ExportSubjectPublicKeyInfo();

        return new SimulatedPeerModel(
            peerId: peerId,
            selfIdentityId: selfIdentityId,
            displayName: displayName,
            isRelayCapable: isRelayCapable,
            identitySigningKeySpki: identitySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: identityPriv,
            endpoint: endpoint);
    }

    /// <summary>
    /// Creates a signed prekey for a peer.
    /// </summary>
    public static (Guid KeyId, byte[] PrivateKey, byte[] PublicKeySpki, byte[] Signature) CreateSignedPreKey(
        byte[] identitySpki,
        byte[] identityPrivateKey)
    {
        var keyId = Guid.NewGuid();
        using var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeySpki = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signedPreKeyPriv = signedPreKey.ExportECPrivateKey();

        using var identityEcdsa = ECDsa.Create();
        identityEcdsa.ImportSubjectPublicKeyInfo(identitySpki, out _);
        identityEcdsa.ImportECPrivateKey(identityPrivateKey, out _);
        var signature = identityEcdsa.SignData(signedPreKeySpki, HashAlgorithmName.SHA256);

        return (keyId, signedPreKeyPriv, signedPreKeySpki, signature);
    }

    /// <summary>
    /// Signs a payload with an identity key.
    /// </summary>
    public static byte[] SignPayload(byte[] payloadBytes, byte[] identitySpki, byte[] identityPrivateKey)
    {
        using var identityEcdsa = ECDsa.Create();
        identityEcdsa.ImportSubjectPublicKeyInfo(identitySpki, out _);
        identityEcdsa.ImportECPrivateKey(identityPrivateKey, out _);
        return identityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Creates an ephemeral key for X3DH handshake.
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKeySpki) CreateEphemeralKey()
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephSpki = eph.PublicKey.ExportSubjectPublicKeyInfo();
        var ephPriv = eph.ExportECPrivateKey();
        return (ephPriv, ephSpki);
    }
}
