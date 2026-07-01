using System;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Features.Chat.State;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class ChatStateServiceTests
{
    private ChatStateService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new ChatStateService();
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
    }

    [Test]
    public void OptimisticInsert_WhenMessageDoesNotExist_AddsMessage()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
        var messageId = PublicMessageId.NewId();
        var snapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: true);

        // Act
        _sut.OptimisticInsert(sessionId, snapshot);

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().HaveCount(1);
        list[0].Id.Should().Be(messageId);
        list[0].IsSending.Value.Should().BeTrue();
    }

    [Test]
    public void OptimisticInsert_WhenMessageAlreadyExists_DoesNotAddDuplicate()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
        var messageId = PublicMessageId.NewId();
        var snapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: true);

        // Act 1: First insert
        _sut.OptimisticInsert(sessionId, snapshot);

        // Act 2: Second insert with same ID
        _sut.OptimisticInsert(sessionId, snapshot);

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().HaveCount(1); // Should not add duplicate
    }

    [Test]
    public void MarkAsDelivered_WhenMessageExists_UpdatesIsDeliveredAndIsSending()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
        var messageId = PublicMessageId.NewId();
        var snapshot = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: true);
        _sut.OptimisticInsert(sessionId, snapshot);

        // Act
        _sut.MarkAsDelivered(sessionId, messageId);

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().HaveCount(1);
        list[0].IsDelivered.Value.Should().BeTrue();
        list[0].IsSending.Value.Should().BeFalse();
    }

    [Test]
    public void MarkAsDelivered_WhenMessageDoesNotExist_DoesNothing()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
        var messageId = PublicMessageId.NewId();

        // Act
        _sut.MarkAsDelivered(sessionId, messageId);

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().BeEmpty();
    }

    [Test]
    public void SyncMessages_WhenSessionEmpty_AddsAllMessages()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
        var messageId1 = PublicMessageId.NewId();
        var messageId2 = PublicMessageId.NewId();
        var snapshots = new[]
        {
            new ChatMessageSnapshot(
                Id: messageId1,
                Author: "Peer",
                Text: "Hi",
                Timestamp: DateTimeOffset.UtcNow,
                IsOwn: false,
                IsDelivered: false,
                IsRead: false,
                IsSending: false),
            new ChatMessageSnapshot(
                Id: messageId2,
                Author: "Me",
                Text: "Hello",
                Timestamp: DateTimeOffset.UtcNow,
                IsOwn: true,
                IsDelivered: false,
                IsRead: false,
                IsSending: false)
        };

        // Act
        _sut.SyncMessages(sessionId, snapshots);

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().HaveCount(2);
    }

    [Test]
    public void SyncMessages_WhenMessageExists_UpdatesFromSnapshot()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
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
        _sut.OptimisticInsert(sessionId, initialSnapshot);

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
        _sut.SyncMessages(sessionId, new[] { updatedSnapshot });

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().HaveCount(1);
        list[0].IsDelivered.Value.Should().BeTrue(); // Should update to true
        list[0].IsSending.Value.Should().BeTrue(); // Should NOT be overwritten (protected by UpdateFromSnapshot)
    }

    [Test]
    public void SyncMessages_WhenSnapshotHasFalseDelivered_DoesNotOverwriteTrueState()
    {
        // Arrange
        var sessionId = new DirectSessionId(Guid.NewGuid());
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
        _sut.OptimisticInsert(sessionId, initialSnapshot);

        // Mark as delivered to set the state
        _sut.MarkAsDelivered(sessionId, messageId);

        var snapshotWithFalseDelivered = new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: "Hello",
            Timestamp: DateTimeOffset.UtcNow,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: false);

        // Act
        _sut.SyncMessages(sessionId, new[] { snapshotWithFalseDelivered });

        // Assert
        var list = _sut.GetOrAddSessionMessagesList(sessionId);
        list.Should().HaveCount(1);
        list[0].IsDelivered.Value.Should().BeTrue(); // Should remain true (protected by UpdateFromSnapshot)
    }
}
