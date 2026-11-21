// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Domain.Channels.ValueObjects;
using R3;

[TestFixture]
public sealed class ChatReloadCoordinatorTests
{
    private FakeTimeProvider _timeProvider = null!;
    private Mock<IChatMessageQueryService> _queryServiceMock = null!;
    private ChannelId _channelId;
    private ChatReloadCoordinator _coordinator = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _queryServiceMock = new Mock<IChatMessageQueryService>();
        _channelId = ChannelId.New();

        _queryServiceMock.Setup(q => q.GetMessagesAsync(
                _channelId,
                It.IsAny<ulong?>(),
                It.IsAny<int>(),
                It.IsAny<ulong>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageReadModel>());

        _coordinator = new ChatReloadCoordinator(
            _channelId,
            _queryServiceMock.Object,
            _timeProvider,
            NullLogger<ChatReloadCoordinator>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _coordinator.Dispose();
    }

    [Test]
    public void TriggerReload_DebouncesRapidTriggersToSingleQuery()
    {
        var emissions = new List<IReadOnlyList<ChatMessageReadModel>>();
        using var sub = _coordinator.MessagesLoaded.Subscribe(msgs => emissions.Add(msgs));

        // Fire 10 rapid triggers in succession
        for (int i = 0; i < 10; i++)
        {
            _coordinator.TriggerReload();
            _timeProvider.Advance(TimeSpan.FromMilliseconds(5));
        }

        // Before debounce window (50ms of quiet) finishes, no reload has occurred
        emissions.Should().BeEmpty();
        _queryServiceMock.Verify(q => q.GetMessagesAsync(It.IsAny<ChannelId>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Never);

        // Advance past the 50ms debounce window
        _timeProvider.Advance(TimeSpan.FromMilliseconds(60));

        // Should have emitted exactly once
        emissions.Should().HaveCount(1);
        _queryServiceMock.Verify(q => q.GetMessagesAsync(It.IsAny<ChannelId>(), It.IsAny<ulong?>(), It.IsAny<int>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ReloadImmediateAsync_ExecutesSynchronouslyWithoutDebounceDelay()
    {
        var msgs = await _coordinator.ReloadImmediateAsync();

        msgs.Should().NotBeNull();
        _queryServiceMock.Verify(q => q.GetMessagesAsync(
            _channelId,
            null,
            100,
            It.IsAny<ulong>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
