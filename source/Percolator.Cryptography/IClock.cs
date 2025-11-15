namespace Percolator.Cryptography;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
