using MediatR;

namespace Percolator.Dht.Messages;

public class FindNodeRequestHandler : IRequestHandler<FindNodeRequest, FindNodeResponse>
{
    private readonly IDhtService _dhtService;

    public FindNodeRequestHandler(IDhtService dhtService)
    {
        _dhtService = dhtService;
    }

    public async Task<FindNodeResponse> Handle(FindNodeRequest request, CancellationToken cancellationToken)
    {
        var closerNodes = await _dhtService.GetClosestNodesAsync(request.TargetId, cancellationToken);

        return new FindNodeResponse(closerNodes.ToList());
    }
}
