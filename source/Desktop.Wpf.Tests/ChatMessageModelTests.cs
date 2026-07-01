using System;
using Desktop.Wpf.Features.Chat;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Chat.Messaging.ValueObjects;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class ChatMessageModelTests
{
    [Test]
    public void UpdateFromSnapshot_WhenSnapshotHasTrueDelivered_UpdatesIsDeliveredToTrue()
    {
        // Arrange
        var messageId = PublicMessageId.NewId();
        var initialSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: true);
        var model = new ChatMessageModel(initialSnapshot);

        var updatedSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: false,
            IsSending: false);

        // Act
        model.UpdateFromSnapshot(updatedSnapshot);

        // Assert
        model.IsDelivered.Value.Should().BeTrue();
    }

    [Test]
    public void UpdateFromSnapshot_WhenSnapshotHasFalseDelivered_DoesNotOverwriteTrueState()
    {
        // Arrange
        var messageId = PublicMessageId.NewId();
        var initialSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: false,
            IsSending: false);
        var model = new ChatMessageModel(initialSnapshot);

        var updatedSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: false);

        // Act
        model.UpdateFromSnapshot(updatedSnapshot);

        // Assert
        model.IsDelivered.Value.Should().BeTrue(); // Should remain true, not overwritten to false
    }

    [Test]
    public void UpdateFromSnapshot_WhenSnapshotHasTrueRead_UpdatesIsReadToTrue()
    {
        // Arrange
        var messageId = PublicMessageId.NewId();
        var initialSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: false,
            IsSending: false);
        var model = new ChatMessageModel(initialSnapshot);

        var updatedSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: true,
            IsSending: false);

        // Act
        model.UpdateFromSnapshot(updatedSnapshot);

        // Assert
        model.IsRead.Value.Should().BeTrue();
    }

    [Test]
    public void UpdateFromSnapshot_WhenSnapshotHasFalseRead_DoesNotOverwriteTrueState()
    {
        // Arrange
        var messageId = PublicMessageId.NewId();
        var initialSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: true,
            IsSending: false);
        var model = new ChatMessageModel(initialSnapshot);

        var updatedSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: false,
            IsSending: false);

        // Act
        model.UpdateFromSnapshot(updatedSnapshot);

        // Assert
        model.IsRead.Value.Should().BeTrue(); // Should remain true, not overwritten to false
    }

    [Test]
    public void UpdateFromSnapshot_DoesNotOverwriteIsSending()
    {
        // Arrange
        var messageId = PublicMessageId.NewId();
        var initialSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: true);
        var model = new ChatMessageModel(initialSnapshot);

        var updatedSnapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: true,
            IsRead: false,
            IsSending: false);

        // Act
        model.UpdateFromSnapshot(updatedSnapshot);

        // Assert
        model.IsSending.Value.Should().BeTrue(); // Should remain true, not overwritten
    }
}
