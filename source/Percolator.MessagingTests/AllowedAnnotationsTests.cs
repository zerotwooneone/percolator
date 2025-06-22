using FluentAssertions;
using NUnit.Framework;
using Percolator.Messaging;

namespace Percolator.MessagingTests
{
    [TestFixture]
    public class AllowedAnnotationsTests
    {
        [TestCase(AllowedAnnotations.ThumbsUp)]
        [TestCase(AllowedAnnotations.ThumbsDown)]
        [TestCase(AllowedAnnotations.Heart)]
        [TestCase(AllowedAnnotations.Smile)]
        [TestCase(AllowedAnnotations.Tada)]
        public void IsAllowed_WithValidEmoji_ReturnsTrue(string emoji)
        {
            // Act
            var result = AllowedAnnotations.IsAllowed(emoji);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void IsAllowed_WithInvalidEmoji_ReturnsFalse()
        {
            // Arrange
            var invalidEmoji = "INVALID_EMOJI";

            // Act
            var result = AllowedAnnotations.IsAllowed(invalidEmoji);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void IsAllowed_WithNullOrEmptyEmoji_ReturnsFalse()
        {
            // Act
            var resultForNull = AllowedAnnotations.IsAllowed(null);
            var resultForEmpty = AllowedAnnotations.IsAllowed(string.Empty);

            // Assert
            resultForNull.Should().BeFalse();
            resultForEmpty.Should().BeFalse();
        }
    }
}
