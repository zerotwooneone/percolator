using MediatR;
using Percolator.Identity;

namespace Percolator.Application.Network;

// Infrastructure failure notification - not a domain event
public class NodeOfflineNotification : INotification
{
    public SelfId IdentityId { get; }
    public string Reason { get; }
    public NodeOfflineNotification(SelfId identityId, string reason)
    {
        IdentityId = identityId;
        Reason = reason;
    }
}
