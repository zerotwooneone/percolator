using Percolator.Domain.Common;

namespace Percolator.Apps.Discovery.Tests.TestDoubles;

/// <summary>
/// Controllable UTC clock for deterministic filter and service tests.
/// </summary>
public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public FakeDateTimeProvider(DateTimeOffset? now = null)
    {
        UtcNow = now ?? DateTimeOffset.UtcNow;
    }

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan duration) => UtcNow += duration;
}
