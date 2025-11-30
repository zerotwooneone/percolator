using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }