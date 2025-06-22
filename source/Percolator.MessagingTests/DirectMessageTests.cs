using System;
using AutoFixture;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Messaging;

namespace Percolator.MessagingTests
{
    [TestFixture]
    public class DirectMessageTests
    {
        private Fixture _fixture;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
        }

        [Test]
        public void Edit_WhenCalled_UpdatesContentAndSetsEditedFlag()
        {
            // Arrange
            var originalContent = _fixture.Create<string>();
            var message = new DirectMessage(Guid.NewGuid(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), originalContent, DateTime.UtcNow);
            var newContent = _fixture.Create<string>();

            // Act
            message.Edit(newContent);

            // Assert
            message.Content.Should().Be(newContent);
            message.IsEdited.Should().BeTrue();
            message.LastEditedTimestampUtc.Should().HaveValue();
            message.LastEditedTimestampUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        }

        [Test]
        public void AddAnnotation_WithValidEmoji_AddsAnnotation()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var peerId = _fixture.Create<string>();
            var validEmoji = AllowedAnnotations.ThumbsUp;

            // Act
            message.AddAnnotation(peerId, validEmoji);

            // Assert
            message.Annotations.Should().ContainSingle(a => a.PeerId == peerId && a.Emoji == validEmoji);
        }

        [Test]
        public void AddAnnotation_WithInvalidEmoji_ThrowsArgumentException()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var peerId = _fixture.Create<string>();
            var invalidEmoji = "INVALID_EMOJI";

            // Act
            Action act = () => message.AddAnnotation(peerId, invalidEmoji);

            // Assert
            act.Should().Throw<ArgumentException>().WithMessage($"Annotation '{invalidEmoji}' is not allowed.");
        }

        [Test]
        public void AddAnnotation_ForSamePeerAndEmoji_DoesNotAddDuplicate()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var peerId = _fixture.Create<string>();
            var emoji = AllowedAnnotations.Heart;
            message.AddAnnotation(peerId, emoji); // Add once

            // Act
            message.AddAnnotation(peerId, emoji); // Add again

            // Assert
            message.Annotations.Should().ContainSingle(a => a.PeerId == peerId && a.Emoji == emoji);
        }

        [Test]
        public void RemoveAnnotation_WithExistingAnnotation_RemovesIt()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var peerId = _fixture.Create<string>();
            var emoji = AllowedAnnotations.Smile;
            message.AddAnnotation(peerId, emoji);

            // Act
            message.RemoveAnnotation(peerId, emoji);

            // Assert
            message.Annotations.Should().NotContain(a => a.PeerId == peerId && a.Emoji == emoji);
        }

        [Test]
        public void RemoveAnnotation_WithNonExistentAnnotation_DoesNothing()
        {
            // Arrange
            var message = _fixture.Create<DirectMessage>();
            var peerId = _fixture.Create<string>();
            var emoji = AllowedAnnotations.Tada;

            // Act
            Action act = () => message.RemoveAnnotation(peerId, emoji);

            // Assert
            act.Should().NotThrow();
            message.Annotations.Should().BeEmpty();
        }
    }
}
