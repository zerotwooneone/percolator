using System;
using Percolator.Cryptography;

namespace Desktop.Wpf.Tests;

public sealed class TestClock : IClock
{
    public static readonly DateTimeOffset Default = DateTimeOffset.Parse("2025-05-01T00:00:00Z");
    public TestClock(DateTimeOffset now) { UtcNow = now; }
    public DateTimeOffset UtcNow { get; }
}