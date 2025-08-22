using MediatR;

namespace Percolator.Dht.Messages;

public class PingRequestHandler : IRequestHandler<PingRequest, PingResponse>
{
    private readonly IDhtNodeRepository _repository;

    public PingRequestHandler(IDhtNodeRepository repository)
    {
        _repository = repository;
    }

    public async Task<PingResponse> Handle(PingRequest request, CancellationToken cancellationToken)
    {
        var existingNode = await _repository.GetAsync(request.SenderId);
        if (existingNode is null)
        {
            var newNode = new DhtNode(request.SenderId, request.SenderEndPoint, DateTimeOffset.UtcNow);
            await _repository.AddAsync(newNode);
        }

        return new PingResponse();
    }
}
