// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Chats;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

[TestFixture]
public sealed class ChatViewModelTests
{
    private ChannelId _channelId;
    private PublicIdentityId _myPersonaId;
    private Mock<IChatService> _chatServiceMock = null!;
    private Mock<IChatMessageQueryService> _queryServiceMock = null!;
    private FakeConversationStateService _conversationStateService = null!;
    private ChatDraftStateService _draftStateService = null!;
    private Mock<ICurrentPersonaService> _currentPersonaServiceMock = null!;
    private FakeTimeProvider _timeProvider = null!;
    private SynchronizationContext? _previousSyncContext;

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [SetUp]
    public void SetUp()
    {
        _previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

        _channelId = ChannelId.New();
        _myPersonaId = PublicIdentityId.New();

        _chatServiceMock = new Mock<IChatService>();
        _queryServiceMock = new Mock<IChatMessageQueryService>();
        _conversationStateService = new FakeConversationStateService();
        _currentPersonaServiceMock = new Mock<ICurrentPersonaService>();
        _currentPersonaServiceMock.Setup(p => p.CurrentPersona)
            .Returns(new ActivePersonaModel(_myPersonaId, "Alice", 5124));
        _currentPersonaServiceMock.Setup(p => p.CurrentPersonaChanged)
            .Returns(R3.Observable.Empty<ActivePersonaModel?>());

        _draftStateService = new ChatDraftStateService(_currentPersonaServiceMock.Object, NullLogger<ChatDraftStateService>.Instance);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        _queryServiceMock.Setup(q => q.GetMessagesAsync(
                _channelId,
                It.IsAny<ulong?>(),
                It.IsAny<int>(),
                It.IsAny<ulong>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageReadModel>());

        _chatServiceMock.Setup(c => c.SendMessageAsync(It.IsAny<SendMessageCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<PayloadId>.Success(PayloadId.New()));
    }

    [TearDown]
    public void TearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        _conversationStateService.Dispose();
        _draftStateService.Dispose();
    }

    private ChatViewModel CreateViewModel()
    {
        return new ChatViewModel(
            _channelId,
            _chatServiceMock.Object,
            _queryServiceMock.Object,
            _conversationStateService,
            _draftStateService,
            _currentPersonaServiceMock.Object,
            _timeProvider,
            NullLogger<ChatViewModel>.Instance,
            NullLogger<ChatReloadCoordinator>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var act1 = () => new ChatViewModel(
            _channelId,
            null!,
            _queryServiceMock.Object,
            _conversationStateService,
            _draftStateService,
            _currentPersonaServiceMock.Object,
            _timeProvider,
            NullLogger<ChatViewModel>.Instance,
            NullLogger<ChatReloadCoordinator>.Instance);
        act1.Should().Throw<ArgumentNullException>().WithParameterName("chatService");

        var act2 = () => new ChatViewModel(
            _channelId,
            _chatServiceMock.Object,
            null!,
            _conversationStateService,
            _draftStateService,
            _currentPersonaServiceMock.Object,
            _timeProvider,
            NullLogger<ChatViewModel>.Instance,
            NullLogger<ChatReloadCoordinator>.Instance);
        act2.Should().Throw<ArgumentNullException>().WithParameterName("queryService");
    }

    [Test]
    public void Constructor_RestoresVolatileDraft()
    {
        _draftStateService.SaveDraft(_myPersonaId, _channelId, new ChatDraftModel("Unsent text", 4, new[] { "att.png" }, 300));

        using var vm = CreateViewModel();

        vm.ComposerText.Should().Be("Unsent text");
        vm.CursorPosition.Should().Be(4);
        vm.ComposerAttachments.Should().ContainSingle().Which.Should().Be("att.png");
        vm.ExpireAfterSeconds.Should().Be(300);
        vm.ExpiringTimerLabel.Should().Be("5m");
    }

    [Test]
    public void ComposerText_WhenChanged_PersistsToVolatileDraftService()
    {
        using var vm = CreateViewModel();
        vm.ComposerText = "Typing new message...";

        var draft = _draftStateService.GetDraft(_myPersonaId, _channelId);
        draft.Text.Should().Be("Typing new message...");
    }

    [Test]
    public void CycleDisappearingTimerCommand_CyclesThroughPresets()
    {
        using var vm = CreateViewModel();
        vm.ExpireAfterSeconds.Should().BeNull();
        vm.ExpiringTimerLabel.Should().Be("Off");

        vm.CycleDisappearingTimerCommand.Execute(null);
        vm.ExpireAfterSeconds.Should().Be(30);
        vm.ExpiringTimerLabel.Should().Be("30s");

        vm.CycleDisappearingTimerCommand.Execute(null);
        vm.ExpireAfterSeconds.Should().Be(300);
        vm.ExpiringTimerLabel.Should().Be("5m");

        vm.CycleDisappearingTimerCommand.Execute(null);
        vm.ExpireAfterSeconds.Should().Be(3600);
        vm.ExpiringTimerLabel.Should().Be("1h");
    }

    [Test]
    public async Task SendMessageCommand_CallsChatService_AndClearsDraftOnSuccess()
    {
        _conversationStateService.RegisterDirectConversation(PublicIdentityId.New(), "Bob", _channelId);

        using var vm = CreateViewModel();
        vm.ComposerText = "Hello Bob!";
        vm.CanSendMessage.Should().BeTrue();

        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.ComposerText.Should().BeEmpty();
        var draft = _draftStateService.GetDraft(_myPersonaId, _channelId);
        draft.Text.Should().BeEmpty();

        _chatServiceMock.Verify(c => c.SendMessageAsync(
            It.Is<SendMessageCommand>(cmd => cmd.ChannelId == _channelId && cmd.Content == "Hello Bob!"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MessageExpiration_TransitionsToTombstone_WhenTimeAdvances()
    {
        var now = _timeProvider.GetUtcNow();
        var expiresAtUnixMs = (ulong)now.AddSeconds(30).ToUnixTimeMilliseconds();

        var message = new ChatMessageReadModel(
            PayloadId.New(),
            _channelId,
            PublicIdentityId.New(),
            (ulong)now.ToUnixTimeMilliseconds(),
            "Self-destructing secret",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            IsTombstone: false,
            Array.Empty<ReactionGroupReadModel>(),
            ExpiresAtUnixMs: expiresAtUnixMs);

        _queryServiceMock.Setup(q => q.GetMessagesAsync(
                _channelId,
                It.IsAny<ulong?>(),
                It.IsAny<int>(),
                It.IsAny<ulong>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatMessageReadModel> { message });

        using var vm = CreateViewModel();
        await Task.Yield(); // Allow initial load to complete

        vm.Messages.Should().ContainSingle();
        var messageVm = vm.Messages.First();
        messageVm.DisplayContent.Should().Be("Self-destructing secret");
        messageVm.IsTombstone.Should().BeFalse();
        messageVm.IsExpired.Should().BeFalse();

        // Advance time past expiration (e.g. 31 seconds)
        _timeProvider.Advance(TimeSpan.FromSeconds(31));

        messageVm.IsExpired.Should().BeTrue();
        messageVm.IsTombstone.Should().BeTrue();
        messageVm.DisplayContent.Should().Be("This message was deleted");
    }

    [Test]
    public void PendingOutboundConversation_SetsPendingBanner_AndDisablesCanSendMessage()
    {
        var targetPeer = PublicIdentityId.New();
        _conversationStateService.BackingList.Add(new ConversationSummaryModel(
            conversationId: _channelId,
            peerOrGroupId: targetPeer,
            displayName: "Charlie",
            routeType: ConversationRouteType.DirectP2P,
            relayId: null,
            lastMessageSnippet: "Waiting...",
            lastActivityUtc: DateTimeOffset.UtcNow,
            unreadCount: 0,
            isPendingOutbound: true,
            deliveryStatus: ConversationDeliveryStatus.Pending));

        using var vm = CreateViewModel();

        vm.IsPendingOutbound.Should().BeTrue();
        vm.ComposerText = "Hello?";
        vm.CanSendMessage.Should().BeFalse();
        vm.SendMessageCommand.CanExecute(null).Should().BeFalse();
    }
}
