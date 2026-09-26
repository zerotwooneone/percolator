using Percolator.Domain.Common;

namespace Percolator.Domain.Tests.TestDoubles;

public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeDateTimeProvider(DateTimeOffset? initialTime = null)
    {
        UtcNow = initialTime ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public void Advance(TimeSpan duration)
    {
        UtcNow = UtcNow.Add(duration);
    }
}
