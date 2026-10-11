using Percolator.Domain.Common;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    private DateTimeOffset _now;

    public FakeDateTimeProvider(DateTimeOffset? initialTime = null)
    {
        _now = initialTime ?? new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow => _now;

    public ulong UtcNowUnixMs => (ulong)_now.ToUnixTimeMilliseconds();

    public void SetTime(DateTimeOffset time)
    {
        _now = time;
    }

    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
    }
}
