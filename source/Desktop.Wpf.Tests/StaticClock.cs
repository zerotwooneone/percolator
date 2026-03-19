using System;
using Percolator.Cryptography;

namespace Desktop.Wpf.Tests;

public sealed class StaticClock : IClock
{
    public static readonly DateTimeOffset DefaultNow = DateTimeOffset.Parse("2026 03 18");
    public StaticClock(DateTimeOffset now) => UtcNow = now;
    public DateTimeOffset UtcNow { get; }
}