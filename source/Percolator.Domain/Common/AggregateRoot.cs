namespace Percolator.Domain.Common;

public interface IAggregateRoot<TId> : IEntity<TId> where TId : IEquatable<TId>
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

public abstract class AggregateRoot<TId> : IAggregateRoot<TId> where TId : IEquatable<TId>
{
    public abstract TId Id { get; }

    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void AddDomainEvent(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}
