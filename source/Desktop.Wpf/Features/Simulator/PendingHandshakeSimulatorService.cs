using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Percolator.Chat.App.Notifications;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Desktop.Wpf.Features.Simulator;

public interface IPendingHandshakeSimulatorService
{
    Task<PendingSessionId> AddSyntheticPendingAsync(PeerId remotePeer, string? displayName = null, byte[]? invitationPayload = null, CancellationToken ct = default);
}

public sealed class PendingHandshakeSimulatorService : IPendingHandshakeSimulatorService
{
    private readonly IPendingSessionRepository _pendingSessions;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly IClock _clock;
    private readonly IMediator _mediator;

    public PendingHandshakeSimulatorService(
        IPendingSessionRepository pendingSessions,
        IPeerIdentityRepository peerIdentityRepository,
        IClock clock,
        IMediator mediator)
    {
        _pendingSessions = pendingSessions;
        _peerIdentityRepository = peerIdentityRepository;
        _clock = clock;
        _mediator = mediator;
    }

    public async Task<PendingSessionId> AddSyntheticPendingAsync(PeerId remotePeer, string? displayName = null, byte[]? invitationPayload = null, CancellationToken ct = default)
    {
        var indentityPeerId = new Percolator.Identity.PeerId(remotePeer.Value);
        var existing = await _peerIdentityRepository.GetByIdAsync(indentityPeerId,ct);
        if (existing == null)
        {
            var peerIdentity = new PeerIdentity(indentityPeerId);
            if(!string.IsNullOrWhiteSpace(displayName))
            {
                peerIdentity.SetDisplayName(displayName);
            }
            await _peerIdentityRepository.SaveAsync(peerIdentity, ct);
        }
        var version = new ProtocolVersion(1);

        // Build a realistic HandshakeInitiatorHello per request (fresh keys)
        // Identity key: ECDSA P-256 (SPKI)
        byte[] identitySpki;
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            identitySpki = ecdsa.ExportSubjectPublicKeyInfo();
        }

        // Ephemeral ratchet key: ECDH P-256 (SPKI)
        byte[] ephSpki;
        using (var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            ephSpki = ecdh.ExportSubjectPublicKeyInfo();
        }

        var hello = new HandshakeInitiatorHello
        {
            InitiatorIdentityKeySpki = ByteString.CopyFrom(identitySpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(ephSpki),
            SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
        };
        if (invitationPayload is not null && invitationPayload.Length > 0)
        {
            hello.EncryptedPayload = ByteString.CopyFrom(invitationPayload);
        }

        var helloBytes = hello.ToByteArray();
        var invitation = new HandshakeInvitation(helloBytes);
        var id = PendingSessionId.NewId();
        var pending = PendingSession.FromInvitation(id, remotePeer, version, invitation, _clock, _clock.UtcNow.AddMinutes(10));
        await _pendingSessions.AddAsync(pending, ct).ConfigureAwait(false);

        await _mediator.Publish(new PendingHandshakeAdded(id, remotePeer, pending.CreatedAtUtc, displayName), ct)
            .ConfigureAwait(false);

        return id;
    }
}
