using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Application.Cryptography;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Sessions;

public sealed class MainInvitationInbox : IMainInvitationInbox
{
    private readonly IPendingHandshakeQueries _pendingQueries;

    public MainInvitationInbox(IPendingHandshakeQueries pendingQueries)
    {
        _pendingQueries = pendingQueries;
    }

    public async Task<IReadOnlyList<PendingInvitationDto>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<PendingInvitationDto>();

        await foreach (var pending in _pendingQueries.EnumerateOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new PendingInvitationDto(
                PendingSessionId: pending.Id.Value,
                RemotePeerId: pending.RemotePeer.Value,
                PeerName: pending.PeerName,
                RequestCorrelationId: pending.RequestCorrelationId.Value,
                CreatedAtUtc: pending.CreatedAtUtc,
                ExpiresAtUtc: pending.ExpiresAtUtc,
                IsRelayed: pending.IsRelayed,
                RelayPeerId: pending.RelayPeer?.Value,
                RelayPeerName: pending.RelayPeerName,
                RelayEndpoint: pending.RelayEndpoint));
        }

        return list;
    }
}

public sealed class MainInvitationOutbox : IMainInvitationOutbox
{
    private readonly ISentInvitationRepository _sent;

    public MainInvitationOutbox(ISentInvitationRepository sent)
    {
        _sent = sent;
    }

    public async Task<IReadOnlyList<SentInvitationDto>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var list = new List<SentInvitationDto>();

        await foreach (var it in _sent.EnumerateExpiredAsync(nowUtc, cancellationToken).ConfigureAwait(false))
        {
            list.Add(new SentInvitationDto(
                RequestCorrelationId: it.RequestCorrelationId.Value,
                SignedPreKeyId: it.SignedPreKeyId,
                OneTimePreKeyId: it.OneTimePreKeyId,
                TargetPeerId: it.TargetPeerId?.Value,
                CreatedAtUtc: it.CreatedAtUtc,
                ExpiresAtUtc: it.ExpiresAtUtc));
        }

        return list;
    }
}

public sealed class MainInvitationActions : IMainInvitationActions
{
    private readonly IMediator _mediator;
    private readonly IPendingSessionRepository _pendingSessions;

    public MainInvitationActions(IMediator mediator, IPendingSessionRepository pendingSessions)
    {
        _mediator = mediator;
        _pendingSessions = pendingSessions;
    }

    public async Task<ApproveInvitationResult> ApproveAsync(Guid pendingSessionId, CancellationToken cancellationToken = default)
    {
        var result = await _mediator
            .Send(new ApprovePendingSessionCommand(new PendingSessionId(pendingSessionId)), cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            ApprovePendingSessionResult.Accepted accepted => new ApproveInvitationResult.Accepted(accepted.SendPath, accepted.RequestCorrelationId.Value),
            ApprovePendingSessionResult.RejectedNotReady => new ApproveInvitationResult.RejectedNotReady(),
            ApprovePendingSessionResult.RejectedInvalid => new ApproveInvitationResult.RejectedInvalid(),
            ApprovePendingSessionResult.RejectedExpired => new ApproveInvitationResult.RejectedExpired(),
            ApprovePendingSessionResult.Failed failed => new ApproveInvitationResult.Failed(failed.ErrorMessage),
            _ => new ApproveInvitationResult.Failed("Unknown result")
        };
    }

    public async Task BurnAsync(Guid pendingSessionId, CancellationToken cancellationToken = default)
    {
        var pending = await _pendingSessions.GetAsync(new PendingSessionId(pendingSessionId), cancellationToken).ConfigureAwait(false);
        if (pending is null) return;

        pending.Reject();
        await _pendingSessions.UpdateAsync(pending, cancellationToken).ConfigureAwait(false);
    }
}
