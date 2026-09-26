namespace Percolator.Domain.Common;

public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
