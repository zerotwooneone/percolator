using Percolator.Identity.SeedWork;

namespace Percolator.Identity.DomainEvents;

// Pure domain event - no MediatR dependency, carries only domain primitive
public class ActiveIdentityLoadedEvent : IDomainEvent
{
    public SelfId IdentityId { get; }
    public ActiveIdentityLoadedEvent(SelfId identityId) => IdentityId = identityId;
}
