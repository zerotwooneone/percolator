using MediatR;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Network;

public abstract record RejectPendingSessionResult
{
    public sealed record Rejected : RejectPendingSessionResult;
    public sealed record NotFound : RejectPendingSessionResult;
    public sealed record Invalid(string ErrorMessage) : RejectPendingSessionResult;
}

public sealed record RejectPendingSessionCommand(PendingSessionId PendingSessionId) : IRequest<RejectPendingSessionResult>;

internal sealed class RejectPendingSessionHandler : IRequestHandler<RejectPendingSessionCommand, RejectPendingSessionResult>
{
    private readonly IPendingSessionRepository _pending;
    private readonly IMediator _mediator;

    public RejectPendingSessionHandler(IPendingSessionRepository pending, IMediator mediator)
    {
        _pending = pending;
        _mediator = mediator;
    }

    public async Task<RejectPendingSessionResult> Handle(RejectPendingSessionCommand request, CancellationToken cancellationToken)
    {
        var pending = await _pending.GetAsync(request.PendingSessionId, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return new RejectPendingSessionResult.NotFound();
        }

        try
        {
            pending.Reject();
        }
        catch (Exception ex)
        {
            return new RejectPendingSessionResult.Invalid(ex.Message);
        }

        var correlationId = pending.RequestCorrelationId;
        await _pending.DeleteAsync(pending.Id, cancellationToken).ConfigureAwait(false);
        await _mediator.Publish(
                new PendingSessionRemovedNotification(pending.Id, correlationId, PendingSessionRemoveReason.Burned),
                cancellationToken)
            .ConfigureAwait(false);
        return new RejectPendingSessionResult.Rejected();
    }
}
