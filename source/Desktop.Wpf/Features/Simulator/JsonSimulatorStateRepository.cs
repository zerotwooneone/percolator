using Desktop.Wpf.Features.Simulator.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Simulator;

public sealed class JsonSimulatorStateRepository : ISimulatorStateRepository, IDisposable
{
    private readonly JsonSerializerOptions _json;
    private readonly string? _overridePath;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly ISimulatedPeerKeyFactory _keys;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public JsonSimulatorStateRepository(
        IOptions<TransportOptions> transportOptions,
        ISimulatedPeerKeyFactory keys,
        IServiceScopeFactory scopeFactory)
        : this(overridePath: null, transportOptions, keys, scopeFactory)
    {
    }

    public JsonSimulatorStateRepository(
        string? overridePath,
        IOptions<TransportOptions> transportOptions,
        ISimulatedPeerKeyFactory keys,
        IServiceScopeFactory scopeFactory)
    {
        _overridePath = overridePath;
        _transportOptions = transportOptions;
        _keys = keys;
        _scopeFactory = scopeFactory;
        _json = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    public async Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var loaded = await ReadPeersFileAsync(cancellationToken).ConfigureAwait(false);
        var state = loaded ?? new SimulatorStateDto { Version = 1 };

        var peerSnaps = new List<PeerStateSnapshot>(state.Peers.Count);
        foreach (var dto in state.Peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dto.PeerId == Guid.Empty) continue;

            NormalizePeer(dto, _transportOptions.Value);
            _ = _keys.EnsureReverseSignalKeys(dto.ReverseSignalKeys);
            _ = EnsureIdentityPublicKeyHash(dto);

            peerSnaps.Add(CreatePeerSnapshot(dto));
        }

        var relSnaps = LoadRelationshipsFromState(state);

        var relaySnaps = new List<RelayStateSnapshot>(state.Relays?.Count ?? 0);
        if (state.Relays is not null)
        {
            foreach (var dto in state.Relays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dto.RelayHostPeerId == Guid.Empty) continue;
                relaySnaps.Add(CreateRelaySnapshot(dto));
            }
        }

        var groups = state.Groups ?? new();

        var snap = new SimulatorStateSnapshot(
            Version: state.Version <= 0 ? 1 : state.Version,
            Peers: peerSnaps,
            Relationships: relSnaps,
            Relays: relaySnaps,
            Groups: groups);

        if (loaded is null)
        {
            await SaveStateAsync(snap, cancellationToken).ConfigureAwait(false);
        }

        return snap;
    }

    public async Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var peers = snapshot.Peers ?? Array.Empty<PeerStateSnapshot>();
            var relationships = snapshot.Relationships ?? Array.Empty<PeerRelationshipSnapshot>();

            var state = new SimulatorStateDto
            {
                Version = snapshot.Version <= 0 ? 1 : snapshot.Version,
                Groups = snapshot.Groups?.ToList() ?? new(),
                Peers = new List<SimulatedPeerDto>(peers.Count),
                Relays = snapshot.Relays
                    ?.Select(CreateRelayDto)
                    .ToList()
                    ?? new()
            };

            foreach (var p in peers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (p.PeerId == Guid.Empty) continue;

                var dto = CreateDto(p);
                NormalizePeer(dto, _transportOptions.Value);
                _ = _keys.EnsureReverseSignalKeys(dto.ReverseSignalKeys);
                _ = EnsureIdentityPublicKeyHash(dto);
                state.Peers.Add(dto);
            }

            ApplyRelationshipsToPeers(state.Peers, relationships);

            await WritePeersFileAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private IClock ResolveClock()
    {
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IClock>();
    }

    private async Task<SimulatorStateDto?> ReadPeersFileAsync(CancellationToken cancellationToken)
    {
        var path = GetStatePath();
        if (!File.Exists(path)) return null;

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<SimulatorStateDto>(fs, _json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task WritePeersFileAsync(SimulatorStateDto state, CancellationToken cancellationToken)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var path = GetStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, state, _json, cancellationToken).ConfigureAwait(false);
        }

        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    private PeerStateSnapshot CreatePeerSnapshot(SimulatedPeerDto dto)
    {
        var clock = ResolveClock();

        var model = new SimulatedPeerModel(
            peerId: dto.PeerId,
            selfIdentityId: dto.SelfIdentityId,
            displayName: dto.DisplayName,
            isOnline: dto.IsOnline,
            isRelayCapable: dto.Relay?.IsRelayCapable == true,
            identitySigningKeySpki: dto.ReverseSignalKeys.IdentitySigningKeySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: dto.ReverseSignalKeys.IdentitySigningKeyPrivateKeyEcPrivateKey,
            connectionMode: dto.Connection?.Mode ?? ConnectionMode.Direct,
            host: dto.Connection?.Host,
            port: dto.Connection?.Port ?? 0,
            relayPeerId: dto.Connection?.RelayPeerId,
            uiState: dto.UiState,
            pendingCorrelationId: dto.PendingCorrelationId,
            targetPublicKeyHash: dto.TargetPublicKeyHash,
            selectedRouteMode: dto.SelectedRouteMode,
            directEndpoint: dto.DirectEndpoint,
            relayHostPeerId: dto.RelayHostPeerId,
            phase: dto.Phase,
            notUntilUtc: dto.NotUntilUtc,
            lastError: dto.LastError,
            knownPeerIds: dto.KnownPeerIds,
            handshakeAttempts: dto.HandshakeAttempts,
            pendingStandardHandshakeToMainResponderPublicKeyHash: dto.PendingStandardHandshakeToMainResponderPublicKeyHash,
            pendingStandardHandshakeToMainTemporarySessionId: dto.PendingStandardHandshakeToMainTemporarySessionId,
            publishedPreKeyBundles: dto.Relay?.PreKeyStore?.PublishedBundles
                ?.Select(b => new SimulatedPublishedPreKeyBundleModel(
                    b.RecipientPublicKeyHash,
                    b.LogicalOwnerPeerId,
                    b.BundleBytes,
                    b.ExpiresUtc))
                .ToList());

        HydrateRuntimeStore(model, dto.RuntimeStore, clock);
        var snap = model.Freeze();
        model.Dispose();
        return snap;
    }

    private static SimulatedPeerDto CreateDto(PeerStateSnapshot model)
    {
        var dto = new SimulatedPeerDto
        {
            PeerId = model.PeerId,
            SelfIdentityId = model.SelfIdentityId,
            DisplayName = model.DisplayName,
            IsOnline = model.IsOnline,
            IdentityPublicKeyHash = SHA256.HashData(model.IdentitySigningKeySpki),
            Connection = new SimulatedPeerConnectionDto
            {
                Mode = model.ConnectionMode,
                Host = model.Host ?? "localhost",
                Port = model.Port,
                RelayPeerId = model.RelayPeerId
            },
            KnownPeerIds = model.KnownPeerIds.ToList(),
            UiState = model.UiState,
            PendingCorrelationId = model.PendingCorrelationId,
            TargetPublicKeyHash = model.TargetPublicKeyHash,
            SelectedRouteMode = model.SelectedRouteMode,
            DirectEndpoint = model.DirectEndpoint,
            RelayHostPeerId = model.RelayHostPeerId,
            Phase = model.Phase,
            NotUntilUtc = model.NotUntilUtc,
            LastError = model.LastError,
            HandshakeAttempts = model.HandshakeAttempts.ToList(),
            PendingStandardHandshakeToMainResponderPublicKeyHash = model.PendingStandardHandshakeToMainResponderPublicKeyHash,
            PendingStandardHandshakeToMainTemporarySessionId = model.PendingStandardHandshakeToMainTemporarySessionId,
            Relay = new SimulatedPeerRelayStateDto
            {
                IsRelayCapable = model.IsRelayCapable,
                PreKeyStore = new SimulatedRelayPreKeyStoreDto
                {
                    Version = 1,
                    PublishedBundles = model.PublishedPreKeyBundles
                        .Select(b => new PublishedPreKeyBundleDto
                        {
                            RecipientPublicKeyHash = b.RecipientPublicKeyHash,
                            LogicalOwnerPeerId = b.LogicalOwnerPeerId,
                            BundleBytes = b.BundleBytes,
                            ExpiresUtc = b.ExpiresUtc
                        })
                        .ToList()
                }
            },
            ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
            {
                Version = 1,
                IdentitySigningKeySpki = model.IdentitySigningKeySpki,
                IdentitySigningKeyPrivateKeyEcPrivateKey = model.IdentitySigningKeyPrivateKeyEcPrivateKey
            },
            RuntimeStore = new SimulatedPeerRuntimeStoreDto
            {
                Version = 1,
                Sessions = model.Sessions
                    .Select(s => new SimulatedSecureSessionDto
                    {
                        SessionId = s.SessionId,
                        RemotePeerId = s.RemotePeerId,
                        ProtocolVersion = s.ProtocolVersion,
                        RootKey = s.RootKey,
                        SendChainKey = s.SendChainKey,
                        SendCounter = s.SendCounter,
                        RecvChainKey = s.RecvChainKey,
                        RecvCounter = s.RecvCounter,
                        PrevChainLength = s.PrevChainLength,
                        RemoteRatchetKey = s.RemoteRatchetKey,
                        DhRatchetPrivateKey = s.DhRatchetPrivateKey,
                        SkippedKeysCount = s.SkippedKeysCount,
                        CreatedAtUtc = s.CreatedAtUtc,
                        LastUsedAtUtc = s.LastUsedAtUtc
                    })
                    .ToList(),
                SignedPreKeys = model.SignedPreKeys
                    .Select(s => new SimulatedSignedPreKeyDto
                    {
                        SignedPreKeyId = s.SignedPreKeyId,
                        PrivateEcPrivateKey = s.PrivateEcPrivateKey,
                        PublicSpki = s.PublicSpki
                    })
                    .ToList(),
                OutboundInvites = model.OutboundInvites
                    .Select(i => new SimulatedOutboundInviteDto
                    {
                        CorrelationId = i.CorrelationId,
                        SignedPreKeyPrivateEcPrivateKey = i.SignedPreKeyPrivateEcPrivateKey
                    })
                    .ToList(),
                PendingInviteHandshakeResponses = model.PendingInviteHandshakeResponses
                    .Select(r => new SimulatedPendingInviteHandshakeResponseDto
                    {
                        CorrelationId = r.CorrelationId,
                        ResponseBytes = r.ResponseBytes
                    })
                    .ToList()
            }
        };

        return dto;
    }

    private static IReadOnlyList<PeerRelationshipSnapshot> LoadRelationshipsFromState(SimulatorStateDto state)
    {
        var edges = new List<PeerRelationshipSnapshot>();
        foreach (var peer in state.Peers)
        {
            if (peer.PeerId == Guid.Empty) continue;

            if (peer.PublishedKeysToPeerIds is not null)
            {
                foreach (var target in peer.PublishedKeysToPeerIds)
                {
                    if (target == Guid.Empty) continue;
                    if (target == peer.PeerId) continue;
                    edges.Add(new PeerRelationshipSnapshot(peer.PeerId, target, RelationshipType.PublishedKey));
                }
            }

            var relayTargets = peer.Relay?.ActiveSessionsPeerIds;
            if (relayTargets is not null)
            {
                foreach (var target in relayTargets)
                {
                    if (target == Guid.Empty) continue;
                    if (target == peer.PeerId) continue;
                    edges.Add(new PeerRelationshipSnapshot(peer.PeerId, target, RelationshipType.RelayActiveSession));
                }
            }
        }

        return edges
            .Distinct()
            .ToList();
    }

    private static void ApplyRelationshipsToPeers(List<SimulatedPeerDto> peers, IReadOnlyList<PeerRelationshipSnapshot> relationships)
    {
        var publishedKeyEdges = new Dictionary<Guid, List<Guid>>();
        var relayActiveEdges = new Dictionary<Guid, List<Guid>>();
        foreach (var rel in relationships)
        {
            if (rel.SourcePeerId == Guid.Empty) continue;
            if (rel.TargetPeerId == Guid.Empty) continue;
            if (rel.SourcePeerId == rel.TargetPeerId) continue;

            if (rel.Type == RelationshipType.PublishedKey)
            {
                if (!publishedKeyEdges.TryGetValue(rel.SourcePeerId, out var list))
                {
                    list = new List<Guid>();
                    publishedKeyEdges[rel.SourcePeerId] = list;
                }
                list.Add(rel.TargetPeerId);
            }
            else if (rel.Type == RelationshipType.RelayActiveSession)
            {
                if (!relayActiveEdges.TryGetValue(rel.SourcePeerId, out var list))
                {
                    list = new List<Guid>();
                    relayActiveEdges[rel.SourcePeerId] = list;
                }
                list.Add(rel.TargetPeerId);
            }
        }

        foreach (var peer in peers)
        {
            peer.PublishedKeysToPeerIds = publishedKeyEdges.TryGetValue(peer.PeerId, out var pk)
                ? pk.Distinct().OrderBy(x => x).ToList()
                : new List<Guid>();

            peer.Relay ??= new SimulatedPeerRelayStateDto();
            peer.Relay.ActiveSessionsPeerIds = relayActiveEdges.TryGetValue(peer.PeerId, out var rs)
                ? rs.Distinct().OrderBy(x => x).ToList()
                : new List<Guid>();
        }
    }

    private static RelayStateSnapshot CreateRelaySnapshot(RelayPersistenceDto dto)
    {
        var upstream = dto.UpstreamToMain
            .Where(m => m.AckId != Guid.Empty)
            .OrderBy(m => m.EnqueuedUtc)
            .Select(m => new OutboundRelayMessageSnapshot(m.AckId, m.OpaqueBytes, m.EnqueuedUtc, m.DebugType))
            .ToList();

        var downstream = dto.DownstreamToPeers
            .Where(m => m.AckId != Guid.Empty)
            .Where(m => m.TargetPkh is not null && m.TargetPkh.Length != 0)
            .OrderBy(m => m.EnqueuedUtc)
            .Select(m => new InboundRelayMessageSnapshot(m.AckId, m.TargetPkh, m.OpaqueBytes, m.EnqueuedUtc, m.DebugType))
            .ToList();

        return new RelayStateSnapshot(
            RelayHostPeerId: dto.RelayHostPeerId,
            UpstreamToMain: upstream,
            DownstreamToPeers: downstream);
    }

    private static RelayPersistenceDto CreateRelayDto(RelayStateSnapshot relay)
    {
        return new RelayPersistenceDto
        {
            Version = 1,
            RelayHostPeerId = relay.RelayHostPeerId,
            UpstreamToMain = relay.UpstreamToMain
                .OrderBy(x => x.EnqueuedUtc)
                .Select(x => new RelayUpstreamMessageDto
                {
                    AckId = x.AckId,
                    OpaqueBytes = x.OpaqueBytes,
                    EnqueuedUtc = x.EnqueuedUtc,
                    DebugType = x.DebugType
                })
                .ToList(),
            DownstreamToPeers = relay.DownstreamToPeers
                .OrderBy(x => x.EnqueuedUtc)
                .Select(x => new RelayDownstreamMessageDto
                {
                    AckId = x.AckId,
                    TargetPkh = x.TargetPkh,
                    OpaqueBytes = x.OpaqueBytes,
                    EnqueuedUtc = x.EnqueuedUtc,
                    DebugType = x.DebugType
                })
                .ToList()
        };
    }

    private static void HydrateRuntimeStore(SimulatedPeerModel model, SimulatedPeerRuntimeStoreDto store, IClock clock)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (clock is null) throw new ArgumentNullException(nameof(clock));

        var crypto = new AeadSessionCrypto();

        foreach (var dto in store.Sessions)
        {
            if (dto.SessionId == Guid.Empty) continue;
            if (dto.RemotePeerId == Guid.Empty) continue;
            if (dto.RootKey is null || dto.RootKey.Length == 0) continue;

            var state = new RatchetState(
                rootKey: new RootKey(dto.RootKey),
                sendingChainKey: dto.SendChainKey is null || dto.SendChainKey.Length == 0 ? null : new ChainKey(dto.SendChainKey),
                sendingCounter: dto.SendCounter,
                receivingChainKey: dto.RecvChainKey is null || dto.RecvChainKey.Length == 0 ? null : new ChainKey(dto.RecvChainKey),
                receivingCounter: dto.RecvCounter,
                previousChainLength: dto.PrevChainLength,
                remoteRatchetKey: dto.RemoteRatchetKey is null || dto.RemoteRatchetKey.Length == 0 ? null : new RatchetEphemeralKey(dto.RemoteRatchetKey),
                dhRatchetPrivateKey: dto.DhRatchetPrivateKey is null || dto.DhRatchetPrivateKey.Length == 0 ? null : new PrivateEphemeralKey(dto.DhRatchetPrivateKey),
                skippedKeyLimit: 1000);

            var session = SecureSession.Create(
                new SessionId(dto.SessionId),
                new PeerId(dto.RemotePeerId),
                new ProtocolVersion(dto.ProtocolVersion <= 0 ? 1 : dto.ProtocolVersion),
                state,
                crypto,
                clock);

            model.SessionsMutable[session.Id] = session;
        }

        foreach (var dto in store.SignedPreKeys)
        {
            if (dto.SignedPreKeyId == Guid.Empty) continue;
            model.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(dto.SignedPreKeyId, dto.PrivateEcPrivateKey, dto.PublicSpki));
        }

        foreach (var dto in store.OutboundInvites)
        {
            if (dto.CorrelationId == Guid.Empty) continue;
            model.OutboundInvitesMutable.Add(new SimulatedOutboundInviteModel(dto.CorrelationId, dto.SignedPreKeyPrivateEcPrivateKey));
        }

        foreach (var dto in store.PendingInviteHandshakeResponses)
        {
            if (dto.CorrelationId == Guid.Empty) continue;
            model.PendingInviteHandshakeResponsesMutable.Add(new SimulatedPendingInviteHandshakeResponseModel(dto.CorrelationId, dto.ResponseBytes));
        }
    }

    private static void NormalizePeer(SimulatedPeerDto peer, TransportOptions transportOptions)
    {
        peer.Connection ??= new SimulatedPeerConnectionDto();
        peer.KnownPeerIds ??= new();
        peer.PreKeys ??= new();
        peer.PreKeys.OneTimePreKeys ??= new();
        peer.RuntimeStore ??= new();
        peer.RuntimeStore.Sessions ??= new();
        peer.RuntimeStore.SignedPreKeys ??= new();
        peer.HandshakeAttempts ??= new();
        peer.Relay ??= new();
        peer.Relay.ActiveSessionsPeerIds ??= new();
        peer.Relay.OpaqueQueue ??= new();
        peer.Relay.OpaqueQueue.Items ??= new();
        peer.Relay.PreKeyStore ??= new();
        peer.Relay.PreKeyStore.PublishedBundles ??= new();
        peer.ReverseSignalKeys ??= new();
        peer.IdentityPublicKeyHash ??= Array.Empty<byte>();

        if (!peer.IsOnline)
        {
            peer.UiState = SimulatorPeerUiState.Offline;
        }
        else if (peer.UiState == SimulatorPeerUiState.Offline)
        {
            peer.UiState = SimulatorPeerUiState.Ready;
            peer.PendingCorrelationId = null;
        }

        if (string.IsNullOrWhiteSpace(peer.Connection.Host) || string.Equals(peer.Connection.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            peer.Connection.Host = AllocateSimulatorLoopbackHost(peer.PeerId);
        }
        if (peer.Connection.Port == 0)
        {
            var port = transportOptions.SimulatorPort;
            if (port == 0) port = 5002;
            peer.Connection.Port = port;
        }
    }

    private static bool EnsureIdentityPublicKeyHash(SimulatedPeerDto peer)
    {
        if (peer is null) throw new ArgumentNullException(nameof(peer));

        var spki = peer.ReverseSignalKeys?.IdentitySigningKeySpki;
        if (spki is null || spki.Length == 0)
        {
            return false;
        }

        var computed = SHA256.HashData(spki);
        if (peer.IdentityPublicKeyHash is not null && peer.IdentityPublicKeyHash.AsSpan().SequenceEqual(computed))
        {
            return false;
        }

        peer.IdentityPublicKeyHash = computed;
        return true;
    }

    private static string AllocateSimulatorLoopbackHost(Guid peerId)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(peerId.ToByteArray());
        var x = (byte)((hash[0] % 254) + 1);
        var y = (byte)((hash[1] % 254) + 1);
        return $"127.77.{x}.{y}";
    }

    private static string GetDefaultStatePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Percolator", "simulator-state.json");
    }

    private string GetStatePath()
    {
        return string.IsNullOrWhiteSpace(_overridePath) ? GetDefaultStatePath() : _overridePath;
    }

    public void Dispose()
    {
    }
}
