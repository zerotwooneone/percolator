using System;
using Percolator.Cryptography;

namespace Desktop.Wpf.Tests;

public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset now) { UtcNow = now; }
    public DateTimeOffset UtcNow { get; }
}