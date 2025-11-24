using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Services;

public sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}