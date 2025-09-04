using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.MessageQueue.Abstractions;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Results;

namespace Percolator.MessageQueue.Handlers;

public class FetchQueuedMessagesHandler : IRequestHandler<FetchQueuedMessagesQuery, FetchQueuedMessagesResult>
{
    private readonly ILogger<FetchQueuedMessagesHandler> _logger;
    private readonly IMessageQueueRepository _repository;

    public FetchQueuedMessagesHandler(
        ILogger<FetchQueuedMessagesHandler> logger,
        IMessageQueueRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    public async Task<FetchQueuedMessagesResult> Handle(FetchQueuedMessagesQuery request, CancellationToken cancellationToken)
    {
        var max = request.MaxCount <= 0 ? 100 : Math.Min(request.MaxCount, 500);
        var blobs = await _repository.FetchAndDeleteAsync(request.RecipientPeerId, max, cancellationToken);
        return new FetchQueuedMessagesResult(blobs);
    }
}
