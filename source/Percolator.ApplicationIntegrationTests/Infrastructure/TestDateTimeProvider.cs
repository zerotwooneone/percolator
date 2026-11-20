using Percolator.Domain.Common;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class TestDateTimeProvider : IDateTimeProvider
{
    private DateTimeOffset _utcNow;

    public TestDateTimeProvider(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow;
    }

    public TestDateTimeProvider() : this(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset UtcNow => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    public void Set(DateTimeOffset newTime) => _utcNow = newTime;
}
