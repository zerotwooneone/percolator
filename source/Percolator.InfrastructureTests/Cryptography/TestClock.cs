using Percolator.Cryptography;

namespace Percolator.InfrastructureTests.Cryptography;

public sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}