using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Cryptography;

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

    public RejectPendingSessionHandler(IPendingSessionRepository pending)
    {
        _pending = pending;
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

        await _pending.UpdateAsync(pending, cancellationToken).ConfigureAwait(false);
        return new RejectPendingSessionResult.Rejected();
    }
}
