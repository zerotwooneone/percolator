using FluentAssertions;
using Percolator.Sessions;

namespace Percolator.SessionsTests;

[TestFixture]
public class GroupConversationTests
{
    [Test]
    public void Constructor_WithValidId_InitializesCorrectly()
    {
        // Arrange
        var groupId = ConversationId.NewId();

        // Act
        var group = new GroupConversation(groupId);

        // Assert
        group.Id.Should().Be(groupId);
    }

    [Test]
    public void Constructor_WithNullId_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new GroupConversation(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}
