using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeRelayedTests
{
    private sealed class DirectoryStub : ISimulatedPeerDirectory
    {
        private readonly ObservableCollection<SimulatedPeerModel> _peers;

        public DirectoryStub(params SimulatedPeerModel[] peers)
        {
            _peers = new ObservableCollection<SimulatedPeerModel>(peers);
            Peers = new ReadOnlyObservableCollection<SimulatedPeerModel>(_peers);
        }

        public ReadOnlyObservableCollection<SimulatedPeerModel> Peers { get; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SimulatedPeerModel> AddPeerAsync(string? displayName, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task RemovePeerAsync(Guid peerId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public void Dispose()
        {
            foreach (var p in _peers)
            {
                p.Dispose();
            }
        }
    }

    private sealed class RelayStateStub : ISimulatorStateService
    {
        private readonly ConcurrentDictionary<(Guid RelayHost, string RoutingKeyB64), Queue<RelayQueuedBlobDto>> _queues = new();
        private readonly ConcurrentDictionary<Guid, SimulatedPeerRuntimeStoreDto> _runtimeByPeerId = new();
        private readonly ConcurrentDictionary<(Guid RelayHost, string PkhB64), Queue<PublishedPreKeyBundleDto>> _preKeyBundles = new();

        public ReadOnlyObservableCollection<SimulatedPeerDto> Peers { get; } = new(new ObservableCollection<SimulatedPeerDto>());

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid> AddPeerAsync(string? displayName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ToggleOnlineAsync(Guid peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ToggleRelayCapableAsync(Guid peerId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateDisplayNameAsync(Guid peerId, string? displayName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetOnlineAsync(Guid peerId, bool isOnline, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetRelayCapableAsync(Guid peerId, bool isRelayCapable, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task SetRuntimeStateAsync(Guid peerId, SimulatorPeerRuntimeState runtimeState, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<Guid?> TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
            return Task.FromResult<Guid?>(null);
        }

        public Task EnqueueRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, byte[] opaqueBytes, string? debugType = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (relayHostPeerId, Convert.ToBase64String(recipientRoutingKey));
            var q = _queues.GetOrAdd(key, _ => new Queue<RelayQueuedBlobDto>());
            q.Enqueue(new RelayQueuedBlobDto
            {
                AckId = Guid.NewGuid(),
                RecipientRoutingKey = recipientRoutingKey,
                OpaqueBytes = opaqueBytes,
                EnqueuedUtc = DateTimeOffset.UtcNow,
                DebugType = debugType
            });
            return Task.CompletedTask;
        }

        public Task<RelayQueuedBlobDto?> PeekRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<RelayQueuedBlobDto>> DequeueRelayOpaqueAsync(Guid relayHostPeerId, byte[] recipientRoutingKey, int max, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (relayHostPeerId, Convert.ToBase64String(recipientRoutingKey));
            if (!_queues.TryGetValue(key, out var q) || q.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<RelayQueuedBlobDto>>(Array.Empty<RelayQueuedBlobDto>());
            }

            var list = new List<RelayQueuedBlobDto>(Math.Min(max, q.Count));
            while (q.Count > 0 && list.Count < max)
            {
                list.Add(q.Dequeue());
            }
            return Task.FromResult<IReadOnlyList<RelayQueuedBlobDto>>(list);
        }

        public Task<bool> DeleteRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> MoveRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, int delta, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delta == 0) return Task.FromResult(false);

            foreach (var kvp in _queues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (kvp.Key.RelayHost != relayHostPeerId) continue;

                var q = kvp.Value;
                lock (q)
                {
                    var list = q.ToArray();
                    var idx = Array.FindIndex(list, x => x.AckId == ackId);
                    if (idx < 0) continue;

                    var newIdx = idx + delta;
                    if (newIdx < 0 || newIdx >= list.Length) return Task.FromResult(false);

                    var item = list[idx];
                    var tmp = new List<RelayQueuedBlobDto>(list);
                    tmp.RemoveAt(idx);
                    tmp.Insert(newIdx, item);

                    q.Clear();
                    foreach (var it in tmp) q.Enqueue(it);
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        public Task<bool> CorruptRelayOpaqueByAckIdAsync(Guid relayHostPeerId, Guid ackId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var kvp in _queues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (kvp.Key.RelayHost != relayHostPeerId) continue;

                var q = kvp.Value;
                lock (q)
                {
                    var list = q.ToArray();
                    var idx = Array.FindIndex(list, x => x.AckId == ackId);
                    if (idx < 0) continue;
                    if (list[idx].OpaqueBytes is null || list[idx].OpaqueBytes.Length == 0) return Task.FromResult(false);

                    var bytes = (byte[])list[idx].OpaqueBytes.Clone();
                    bytes[0] = (byte)(bytes[0] ^ 0x01);
                    list[idx].OpaqueBytes = bytes;

                    q.Clear();
                    foreach (var it in list) q.Enqueue(it);
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        public Task PublishPreKeyBundleAsync(
            Guid relayHostPeerId,
            byte[] recipientPublicKeyHash,
            Guid logicalOwnerPeerId,
            byte[] bundleBytes,
            DateTimeOffset expiresUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
            if (bundleBytes is null) throw new ArgumentNullException(nameof(bundleBytes));

            var key = (relayHostPeerId, Convert.ToBase64String(recipientPublicKeyHash));
            var q = _preKeyBundles.GetOrAdd(key, _ => new Queue<PublishedPreKeyBundleDto>());
            q.Enqueue(new PublishedPreKeyBundleDto
            {
                RecipientPublicKeyHash = recipientPublicKeyHash,
                LogicalOwnerPeerId = logicalOwnerPeerId,
                BundleBytes = bundleBytes,
                ExpiresUtc = expiresUtc
            });
            return Task.CompletedTask;
        }

        public Task<PublishedPreKeyBundleDto?> TryPopPreKeyBundleByRecipientPkhAsync(
            Guid relayHostPeerId,
            byte[] recipientPublicKeyHash,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));

            var key = (relayHostPeerId, Convert.ToBase64String(recipientPublicKeyHash));
            if (!_preKeyBundles.TryGetValue(key, out var q) || q.Count == 0)
            {
                return Task.FromResult<PublishedPreKeyBundleDto?>(null);
            }

            // best-effort expiration handling
            var now = DateTimeOffset.UtcNow;
            while (q.Count > 0 && q.Peek().ExpiresUtc <= now)
            {
                _ = q.Dequeue();
            }
            if (q.Count == 0)
            {
                return Task.FromResult<PublishedPreKeyBundleDto?>(null);
            }

            return Task.FromResult<PublishedPreKeyBundleDto?>(q.Dequeue());
        }

        public Task<SimulatedPeerRuntimeStoreDto?> TryGetRuntimeStoreAsync(Guid peerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runtimeByPeerId.TryGetValue(peerId, out var store);
            return Task.FromResult<SimulatedPeerRuntimeStoreDto?>(store);
        }

        public Task SaveRuntimeStoreAsync(Guid peerId, SimulatedPeerRuntimeStoreDto store, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runtimeByPeerId[peerId] = store;
            return Task.CompletedTask;
        }

        public Task AddPublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemovePublishedKeysRelationshipAsync(Guid publisherPeerId, Guid hostPeerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task AddRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveRelayActiveSessionAsync(Guid relayHostPeerId, Guid peerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Relayed_invite_and_response_can_be_accepted_and_finalized()
    {
        var pending = new SimulatedPeerPendingInbox();

        var relayHostPeerId = Guid.NewGuid();
        var inviterPeerId = Guid.NewGuid();
        var acceptorPeerId = Guid.NewGuid();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterIdentityPriv = inviterIdentityEcdh.ExportECPrivateKey();
        using var inviterIdentityEcdsa = ECDsa.Create(inviterIdentityEcdh.ExportParameters(true));
        var inviterIdentitySpki = inviterIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var inviterModel = new SimulatedPeerModel(inviterPeerId, "inviter", isOnline: true, isRelayCapable: false, inviterIdentitySpki, inviterIdentityPriv);
        var acceptorModel = new SimulatedPeerModel(acceptorPeerId, "acceptor", isOnline: true, isRelayCapable: false, acceptorIdentitySpki, acceptorIdentityPriv);

        using var directory = new DirectoryStub(inviterModel, acceptorModel);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(PercolatorMessageService));
        var stateForRuntime = new Moq.Mock<ISimulatorStateService>(Moq.MockBehavior.Loose);

        var diagnostics = new SimulatorDiagnosticsService();
        var runtime = new SimulatedPeerRuntimeService(directory, messageService, stateForRuntime.Object, diagnostics, pending);

        var relayState = new RelayStateStub();
        var relay = new SimulatorRelayEmulator(relayState, runtime, messageService);

        using var inviterSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var inviterSignedPreKeyPriv = inviterSignedPreKey.ExportECPrivateKey();
        var preKeySig = inviterIdentityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var correlation = Guid.NewGuid();
        runtime.RecordOutboundInviteSignedPreKeyPrivate(inviterPeerId, correlation, inviterSignedPreKeyPriv);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.77.1.1",
            InviterPort = 5002,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = inviterIdentityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var invite = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };

        await relay.EnqueueToRelayHostAsync(relayHostPeerId, acceptorPeerId, invite.ToByteArray(), debugType: nameof(EstablishDirectSessionRequest), cancellationToken: CancellationToken.None);

        var inviteFromRelay = await relay.FetchFromRelayHostAsync(relayHostPeerId, acceptorPeerId, max: 1, cancellationToken: CancellationToken.None);
        inviteFromRelay.Should().HaveCount(1);
        var parsedInvite = EstablishDirectSessionRequest.Parser.ParseFrom(inviteFromRelay[0].OpaqueBytes);

        var acceptance = await runtime.AcceptReverseSignalInviteAsync(
            simulatedPeerId: acceptorPeerId,
            inviterPeerId: inviterPeerId,
            invite: parsedInvite,
            cancellationToken: CancellationToken.None);

        await relay.EnqueueToRelayHostAsync(relayHostPeerId, inviterPeerId, acceptance.Response.ToByteArray(), debugType: nameof(InviteHandshakeResponse), cancellationToken: CancellationToken.None);

        var responseFromRelay = await relay.FetchFromRelayHostAsync(relayHostPeerId, inviterPeerId, max: 1, cancellationToken: CancellationToken.None);
        responseFromRelay.Should().HaveCount(1);
        var parsedResponse = InviteHandshakeResponse.Parser.ParseFrom(responseFromRelay[0].OpaqueBytes);

        pending.AddInviteHandshakeResponse(inviterPeerId, correlation, parsedResponse);

        var finalizedSid = await runtime.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        finalizedSid.Should().NotBeNull();
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }
}
