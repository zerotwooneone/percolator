using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator;

public interface IPendingHandshakeSimulatorService
{
    Task<RequestCorrelationId> AddSyntheticPendingAsync(string? displayName = null, CancellationToken ct = default);

    Task<IReadOnlyList<RequestCorrelationId>> AddSyntheticPendingsAsync(int count, CancellationToken ct = default);
    IReadOnlyList<SimulatedPeerSnapshot> SnapshotPeers();
    int CorrelateOutboundSnapshot();
}

public enum SimulatedPeerState
{
    PendingInvite = 0,
    OutboundResponseObserved = 1
}

public sealed record SimulatedPeerSnapshot(
    RequestCorrelationId RequestCorrelationId,
    SimulatedPeerState State,
    string? DisplayName);

public sealed class PendingHandshakeSimulatorService : IPendingHandshakeSimulatorService
{
    private sealed class SimulatedPeer
    {
        public required RequestCorrelationId CorrelationId;
        public string? DisplayName;
        public SimulatedPeerState State;
    }

    private readonly IEstablishDirectSessionService _establish;
    private readonly IOutboundMessageWireTap _wireTap;

    private readonly Dictionary<Guid, SimulatedPeer> _peersByCorrelation = new();
    private readonly HashSet<string> _seenOutbound = new(StringComparer.Ordinal);

    public PendingHandshakeSimulatorService(
        IEstablishDirectSessionService establish,
        IOutboundMessageWireTap wireTap)
    {
        _establish = establish ?? throw new ArgumentNullException(nameof(establish));
        _wireTap = wireTap ?? throw new ArgumentNullException(nameof(wireTap));
    }

    public async Task<RequestCorrelationId> AddSyntheticPendingAsync(string? displayName = null, CancellationToken ct = default)
    {
        var correlation = new RequestCorrelationId(Guid.NewGuid());

        // Inviter identity key (ECDSA P-256)
        using var inviterEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inviterSpki = inviterEcdsa.ExportSubjectPublicKeyInfo();

        // Signed pre-key bundle: we only need a signed pre-key SPKI + signature
        using var inviterSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var preKeySig = inviterEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "example.com",
            InviterPort = 443,
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
        var payloadSig = inviterEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        _ = await _establish.QueueInviteAsync(inviterSpki, payloadBytes, payloadSig, isRelayed: false, ct).ConfigureAwait(false);

        _peersByCorrelation[correlation.Value] = new SimulatedPeer
        {
            CorrelationId = correlation,
            DisplayName = displayName,
            State = SimulatedPeerState.PendingInvite
        };

        return correlation;
    }

    public async Task<IReadOnlyList<RequestCorrelationId>> AddSyntheticPendingsAsync(int count, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<RequestCorrelationId>();

        var list = new List<RequestCorrelationId>(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var correlation = new RequestCorrelationId(Guid.NewGuid());
            var display = $"SimPeer-{correlation.Value.ToString()[..8]}";

            // Inviter identity key (ECDSA P-256)
            using var inviterEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var inviterSpki = inviterEcdsa.ExportSubjectPublicKeyInfo();

            // Signed pre-key bundle: we only need a signed pre-key SPKI + signature
            using var inviterSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
            var preKeySig = inviterEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 443,
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
            var payloadSig = inviterEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

            // Inject via real reverse-signal ingress path.
            _ = await _establish.QueueInviteAsync(inviterSpki, payloadBytes, payloadSig, isRelayed: false, ct).ConfigureAwait(false);

            _peersByCorrelation[correlation.Value] = new SimulatedPeer
            {
                CorrelationId = correlation,
                DisplayName = display,
                State = SimulatedPeerState.PendingInvite
            };

            list.Add(correlation);
        }

        return list;
    }

    public IReadOnlyList<SimulatedPeerSnapshot> SnapshotPeers()
    {
        return _peersByCorrelation.Values
            .Select(p => new SimulatedPeerSnapshot(p.CorrelationId, p.State, p.DisplayName))
            .OrderByDescending(p => p.RequestCorrelationId.Value)
            .ToList();
    }

    public int CorrelateOutboundSnapshot()
    {
        var messages = _wireTap.Snapshot();
        if (messages.Count == 0) return 0;

        var advanced = 0;
        foreach (var msg in messages)
        {
            if (!string.Equals(msg.MessageType, nameof(InviteHandshakeResponse), StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(msg.RequestCorrelationId))
            {
                continue;
            }

            // De-dupe by request_correlation_id + message type + destination
            var key = $"{msg.MessageType}:{msg.DestinationPeerId.Value}:{msg.RequestCorrelationId}";
            if (!_seenOutbound.Add(key))
            {
                continue;
            }

            if (Guid.TryParse(msg.RequestCorrelationId, out var guid)
                && _peersByCorrelation.TryGetValue(guid, out var peer))
            {
                if (peer.State != SimulatedPeerState.OutboundResponseObserved)
                {
                    peer.State = SimulatedPeerState.OutboundResponseObserved;
                    advanced++;
                }
            }
        }

        return advanced;
    }
}
