namespace Percolator.Domain.Common;

public interface IEntity<TId> where TId : IEquatable<TId>
{
    TId Id { get; }
}
