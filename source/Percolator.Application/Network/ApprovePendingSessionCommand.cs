using MediatR;
using Percolator.Application.ReverseSignal;
using Percolator.Cryptography;

namespace Percolator.Application.Network
{
    public sealed record ApprovePendingSessionCommand(PendingSessionId PendingSessionId) : IRequest<bool>;

    internal sealed class ApprovePendingSessionHandler : IRequestHandler<ApprovePendingSessionCommand, bool>
    {
        private readonly ReverseSignalAcceptService _acceptService;

        public ApprovePendingSessionHandler(ReverseSignalAcceptService acceptService)
        {
            _acceptService = acceptService;
        }

        public async Task<bool> Handle(ApprovePendingSessionCommand request, CancellationToken cancellationToken)
        {
            return await _acceptService.AcceptAsync(request.PendingSessionId, cancellationToken).ConfigureAwait(false);
        }
    }
}
