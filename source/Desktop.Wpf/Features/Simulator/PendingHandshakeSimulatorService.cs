using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Chat.App.Notifications;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator;

public interface IPendingHandshakeSimulatorService
{
    Task<PendingSessionId> AddSyntheticPendingAsync(PeerId remotePeer, string? displayName = null, byte[]? invitationPayload = null, CancellationToken ct = default);
}

public sealed class PendingHandshakeSimulatorService : IPendingHandshakeSimulatorService
{
    private readonly IHandshakeInvitationFactory _invites;
    private readonly IPendingSessionRepository _pendingSessions;
    private readonly IClock _clock;
    private readonly IMediator _mediator;

    public PendingHandshakeSimulatorService(
        IHandshakeInvitationFactory invites,
        IPendingSessionRepository pendingSessions,
        IClock clock,
        IMediator mediator)
    {
        _invites = invites;
        _pendingSessions = pendingSessions;
        _clock = clock;
        _mediator = mediator;
    }

    public async Task<PendingSessionId> AddSyntheticPendingAsync(PeerId remotePeer, string? displayName = null, byte[]? invitationPayload = null, CancellationToken ct = default)
    {
        var version = new ProtocolVersion(1);
        var invitation = _invites.CreateSynthetic(remotePeer, version, invitationPayload);
        var id = PendingSessionId.NewId();
        var pending = PendingSession.FromInvitation(id, remotePeer, version, invitation, _clock, _clock.UtcNow.AddMinutes(10));
        await _pendingSessions.AddAsync(pending, ct).ConfigureAwait(false);

        await _mediator.Publish(new PendingHandshakeAdded(id, remotePeer, pending.CreatedAtUtc, displayName), ct)
            .ConfigureAwait(false);

        return id;
    }
}
