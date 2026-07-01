using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat.MessageQueue.Commands;
using Percolator.Application.Chat.MessageQueue.Results;

namespace Percolator.Application.Chat.MessageQueue.Handlers;

public class FetchQueuedMessagesHandler : IRequestHandler<FetchQueuedMessagesQuery, FetchQueuedMessagesResult>
{
    private readonly ILogger<FetchQueuedMessagesHandler> _logger;
    private readonly IMessageQueueRepository _repository;
    private readonly IPeerIdentityQueries _peerIdentityQueries;

    public FetchQueuedMessagesHandler(
        ILogger<FetchQueuedMessagesHandler> logger,
        IMessageQueueRepository repository,
        IPeerIdentityQueries peerIdentityQueries)
    {
        _logger = logger;
        _repository = repository;
        _peerIdentityQueries = peerIdentityQueries;
    }

    public async Task<FetchQueuedMessagesResult> Handle(FetchQueuedMessagesQuery request, CancellationToken cancellationToken)
    {
        var pkh = await _peerIdentityQueries.GetPublicKeyHashAsync(request.RecipientPeerId, cancellationToken).ConfigureAwait(false);
        if(pkh is null)
            throw new InvalidOperationException($"Public identity key not found for peer {request.RecipientPeerId}");
        var max = request.MaxCount <= 0 ? 100 : Math.Min(request.MaxCount, 500);
        var items = await _repository.FetchAsync(pkh, max, cancellationToken).ConfigureAwait(false);
        var blobs = items.Select(x => x.Blob.ToArray()).ToList();
        return new FetchQueuedMessagesResult(blobs);
    }
}
