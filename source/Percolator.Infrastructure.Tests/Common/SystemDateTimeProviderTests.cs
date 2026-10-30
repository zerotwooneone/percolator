namespace Percolator.Infrastructure.Tests.Common;

using Percolator.Infrastructure.Common;

[TestFixture]
public class SystemDateTimeProviderTests
{
    [Test]
    public void UtcNow_ReturnsCurrentTime()
    {
        // Arrange
        var provider = new SystemDateTimeProvider();
        var before = DateTimeOffset.UtcNow;

        // Act
        var now = provider.UtcNow;
        var after = DateTimeOffset.UtcNow;

        // Assert
        now.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
