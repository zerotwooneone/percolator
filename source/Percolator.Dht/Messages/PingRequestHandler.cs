using MediatR;

namespace Percolator.Dht.Messages;

public class PingRequestHandler : IRequestHandler<PingRequest, PingResponse>
{
    private readonly IDhtNodeRepository _repository;

    public PingRequestHandler(IDhtNodeRepository repository)
    {
        _repository = repository;
    }

    public Task<PingResponse> Handle(PingRequest request, CancellationToken cancellationToken)
    {
        // TODO: Logic to update the sender's node information in the repository.
        return Task.FromResult(new PingResponse());
    }
}
