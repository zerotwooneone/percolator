using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat.MessageQueue.Commands;
using Percolator.Application.Chat.MessageQueue.Results;

namespace Percolator.Application.Chat.MessageQueue.Handlers;

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
        var items = await _repository.FetchAsync(request.RecipientPeerId, max, cancellationToken);
        var blobs = items.Select(x => x.Blob.ToArray()).ToList();
        return new FetchQueuedMessagesResult(blobs);
    }
}
