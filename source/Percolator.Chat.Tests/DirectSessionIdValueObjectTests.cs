using FluentAssertions;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class DirectSessionIdValueObjectTests
{
    [Test]
    public void FromGuid_WhenGuidIsNull_ReturnsNull()
    {
        // Arrange
        Guid? nullGuid = null;

        // Act
        var result = DirectSessionIdValueObject.FromGuid(nullGuid);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void FromGuid_WhenGuidHasValue_ReturnsValueObject()
    {
        // Arrange
        var guid = Guid.NewGuid();

        // Act
        var result = DirectSessionIdValueObject.FromGuid(guid);

        // Assert
        result.Should().NotBeNull();
        result!.Value.Value.Should().Be(guid);
    }

    [Test]
    public void ToString_ReturnsGuidStringRepresentation()
    {
        // Arrange
        var guid = Guid.NewGuid();
        var valueObject = DirectSessionIdValueObject.FromGuid(guid)!;

        // Act
        var result = valueObject.ToString();

        // Assert
        result.Should().Be(guid.ToString());
    }
}
