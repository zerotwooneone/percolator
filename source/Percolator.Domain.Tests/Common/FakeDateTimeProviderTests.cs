using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Common;

[TestFixture]
public class FakeDateTimeProviderTests
{
    [Test]
    public void Advance_IncrementsVirtualTimeAccurately()
    {
        var initialTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var provider = new FakeDateTimeProvider(initialTime);

        provider.UtcNow.Should().Be(initialTime);

        provider.Advance(TimeSpan.FromMinutes(30));

        provider.UtcNow.Should().Be(initialTime.AddMinutes(30));
    }
}
