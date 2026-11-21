// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using R3;

/// <summary>
/// Primary conversation ViewModel governing active chat transcripts, real-time message ingress,
/// volatile draft persistence, and disappearing message expiration countdowns ([DESK-07], [VM-001]).
/// </summary>
public sealed partial class ChatViewModel : ViewModelBase
{
    private readonly ChannelId _channelId;
    private readonly IChatService _chatService;
    private readonly IChatMessageQueryService _queryService;
    private readonly IConversationStateService _conversationStateService;
    private readonly IChatDraftStateService _draftStateService;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatViewModel> _logger;

    private readonly ObservableList<ChatMessageViewModel> _messagesList = new();
    private readonly Dictionary<PayloadId, ChatMessageModel> _modelsById = new();
    private readonly ChatReloadCoordinator _reloadCoordinator;
    private readonly object _lock = new();

    public ChannelId ChannelId => _channelId;

    [ObservableProperty]
    private string _conversationTitle = "Conversation";

    [ObservableProperty]
    private ConversationRouteType _routeType = ConversationRouteType.DirectP2P;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isPendingOutbound;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _composerText = string.Empty;

    [ObservableProperty]
    private int _cursorPosition;

    [ObservableProperty]
    private int? _expireAfterSeconds;

    [ObservableProperty]
    private bool _isExpiringMessagesEnabled;

    [ObservableProperty]
    private string _expiringTimerLabel = "Off";

    [ObservableProperty]
    private bool _isLoading;

    public INotifyCollectionChangedSynchronizedViewList<ChatMessageViewModel> Messages { get; }

    public ObservableList<string> ComposerAttachments { get; } = new();

    public bool CanSendMessage => !IsPendingOutbound &&
        (!string.IsNullOrWhiteSpace(ComposerText) || ComposerAttachments.Count > 0);

    public ChatViewModel(
        ChannelId channelId,
        IChatService chatService,
        IChatMessageQueryService queryService,
        IConversationStateService conversationStateService,
        IChatDraftStateService draftStateService,
        ICurrentPersonaService currentPersonaService,
        TimeProvider timeProvider,
        ILogger<ChatViewModel> logger,
        ILogger<ChatReloadCoordinator> reloadCoordinatorLogger)
    {
        _channelId = channelId;
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _conversationStateService = conversationStateService ?? throw new ArgumentNullException(nameof(conversationStateService));
        _draftStateService = draftStateService ?? throw new ArgumentNullException(nameof(draftStateService));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(reloadCoordinatorLogger);

        var view = _messagesList.CreateView(x => x);
        Messages = SynchronizationContext.Current != null
            ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            : view.ToNotifyCollectionChanged();
        Messages.AddTo(ref Bag);

        _reloadCoordinator = new ChatReloadCoordinator(_channelId, _queryService, _timeProvider, reloadCoordinatorLogger);
        _reloadCoordinator.AddTo(ref Bag);

        _reloadCoordinator.MessagesLoaded
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(OnMessagesLoaded)
            .AddTo(ref Bag);

        // Track pending connection and title from ConversationStateService
        _conversationStateService.Conversations.ObserveChanged()
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => UpdateConversationMetadata())
            .AddTo(ref Bag);

        _conversationStateService.ConnectionSucceeded
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => UpdateConversationMetadata())
            .AddTo(ref Bag);

        UpdateConversationMetadata();
        RestoreVolatileDraft();

        // Expiring message countdown timer: ticks every 1 second
        Observable.Interval(TimeSpan.FromSeconds(1), _timeProvider)
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => TickExpirations())
            .AddTo(ref Bag);

        // Initial message load
        _ = LoadInitialMessagesAsync();
    }

    private async Task LoadInitialMessagesAsync()
    {
        IsLoading = true;
        try
        {
            var msgs = await _reloadCoordinator.ReloadImmediateAsync().ConfigureAwait(true);
            OnMessagesLoaded(msgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load initial messages for channel {ChannelId}", _channelId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnMessagesLoaded(IReadOnlyList<ChatMessageReadModel> readModels)
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        var myId = currentPersona?.IdentityId ?? default;
        var nowUtc = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            foreach (var readModel in readModels)
            {
                var isOutgoing = myId.IsValid && readModel.AuthorId == myId;
                var timestampUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)readModel.TimestampUnixMs);

                var deliveryStatus = MessageDeliveryStatus.Sent;
                if (readModel.ReadAtUnixMs.HasValue) deliveryStatus = MessageDeliveryStatus.Read;
                else if (readModel.DeliveredAtUnixMs.HasValue) deliveryStatus = MessageDeliveryStatus.Delivered;

                var reactions = readModel.Reactions.Select(r =>
                {
                    var emojiRes = Emoji.Create(r.Emoji);
                    var emojiVal = emojiRes.IsSuccess ? emojiRes.Value : default;
                    return new ReactionGroupModel(
                        emojiVal,
                        r.Count,
                        r.HasReactedByCurrentUser || (myId.IsValid && r.Reactors.Contains(myId)));
                }).ToList();

                var attachmentNames = readModel.Attachments
                    .Select(a => a.FileName ?? string.Empty)
                    .ToList();

                DateTimeOffset? expiresAtUtc = readModel.ExpiresAtUnixMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)readModel.ExpiresAtUnixMs.Value)
                    : null;

                if (_modelsById.TryGetValue(readModel.Id, out var existing))
                {
                    existing.Content = readModel.Content;
                    existing.IsTombstone = readModel.IsTombstone;
                    existing.DeliveryStatus = deliveryStatus;
                    existing.Reactions = reactions;
                    existing.AttachmentFileNames = attachmentNames;
                    if (expiresAtUtc.HasValue)
                    {
                        existing.ExpiresAtUtc = expiresAtUtc;
                    }
                    existing.UpdateExpiration(nowUtc);
                }
                else
                {
                    var model = new ChatMessageModel(
                        readModel.Id,
                        _channelId,
                        readModel.AuthorId,
                        isOutgoing ? "You" : $"Peer {readModel.AuthorId.Value:N}"[..12],
                        readModel.Content,
                        timestampUtc,
                        isOutgoing,
                        deliveryStatus,
                        readModel.IsTombstone,
                        expiresAtUtc,
                        reactions,
                        attachmentNames);

                    model.UpdateExpiration(nowUtc);
                    _modelsById[readModel.Id] = model;

                    var vm = new ChatMessageViewModel(
                        model,
                        onAddReaction: (msgVm, emoji) => HandleAddReactionAsync(msgVm, emoji),
                        onDeleteMessage: msgVm => HandleDeleteMessageAsync(msgVm));

                    _messagesList.Add(vm);
                }
            }
        }
    }

    private void UpdateConversationMetadata()
    {
        var match = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
        if (match != null)
        {
            ConversationTitle = match.DisplayName;
            RouteType = match.RouteType;
            IsPendingOutbound = match.IsPendingOutbound;
        }
    }

    private void RestoreVolatileDraft()
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid) return;

        var draft = _draftStateService.GetDraft(currentPersona.IdentityId, _channelId);
        ComposerText = draft.Text;
        CursorPosition = draft.CursorPosition;
        ExpireAfterSeconds = draft.ExpireAfterSeconds;
        UpdateExpirationLabel();

        ComposerAttachments.Clear();
        foreach (var att in draft.Attachments)
        {
            ComposerAttachments.Add(att);
        }

        OnPropertyChanged(nameof(ComposerText));
        OnPropertyChanged(nameof(CursorPosition));
        OnPropertyChanged(nameof(CanSendMessage));
    }

    partial void OnComposerTextChanged(string value)
    {
        SaveVolatileDraft();
    }

    partial void OnExpireAfterSecondsChanged(int? value)
    {
        UpdateExpirationLabel();
        SaveVolatileDraft();
    }

    private void UpdateExpirationLabel()
    {
        IsExpiringMessagesEnabled = ExpireAfterSeconds.HasValue && ExpireAfterSeconds.Value > 0;
        ExpiringTimerLabel = ExpireAfterSeconds switch
        {
            30 => "30s",
            300 => "5m",
            3600 => "1h",
            86400 => "1d",
            604800 => "1w",
            2419200 => "4w",
            _ => "Off"
        };
    }

    private void SaveVolatileDraft()
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid) return;

        var draft = new ChatDraftModel(
            ComposerText,
            CursorPosition,
            ComposerAttachments.ToList(),
            ExpireAfterSeconds);

        _draftStateService.SaveDraft(currentPersona.IdentityId, _channelId, draft);
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid)
        {
            _logger.LogWarning("Cannot send message: no active persona");
            return;
        }

        var text = ComposerText.Trim();
        if (string.IsNullOrEmpty(text) && ComposerAttachments.Count == 0) return;

        var conversation = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
        PublicIdentityId? recipientId = conversation?.PeerOrGroupId;

        var route = conversation != null && conversation.RouteType == ConversationRouteType.Relayed
            ? DeliveryRoute.RelayedOneToOne
            : DeliveryRoute.DirectP2P;

        var command = new SendMessageCommand(
            ChannelId: _channelId,
            AuthorId: currentPersona.IdentityId,
            RecipientIdentityId: recipientId,
            RecipientDeviceId: DeviceId.Primary,
            Route: route,
            Content: text,
            Mentions: null,
            ReplyContext: null,
            LinkPreview: null,
            Attachments: null,
            ExpireAfterSeconds: ExpireAfterSeconds);

        try
        {
            var result = await _chatService.SendMessageAsync(command).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                ComposerText = string.Empty;
                ComposerAttachments.Clear();
                _draftStateService.ClearDraft(currentPersona.IdentityId, _channelId);

                // Optimistically request reload to pick up saved message
                _reloadCoordinator.TriggerReload();
            }
            else
            {
                _logger.LogWarning("Failed to send message: {Error}", result.Error.Description);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending message on channel {ChannelId}", _channelId);
        }
    }

    private async Task HandleAddReactionAsync(ChatMessageViewModel msgVm, Emoji emoji)
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid) return;

        var conversation = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
        var route = conversation != null && conversation.RouteType == ConversationRouteType.Relayed
            ? DeliveryRoute.RelayedOneToOne
            : DeliveryRoute.DirectP2P;

        var command = new AddReactionCommand(
            TargetPayloadId: msgVm.Id,
            ChannelId: _channelId,
            AuthorId: currentPersona.IdentityId,
            RecipientIdentityId: conversation?.PeerOrGroupId,
            RecipientDeviceId: DeviceId.Primary,
            Route: route,
            Emoji: emoji);

        await _chatService.AddReactionAsync(command).ConfigureAwait(true);
        _reloadCoordinator.TriggerReload();
    }

    private async Task HandleDeleteMessageAsync(ChatMessageViewModel msgVm)
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid) return;

        var conversation = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
        var route = conversation != null && conversation.RouteType == ConversationRouteType.Relayed
            ? DeliveryRoute.RelayedOneToOne
            : DeliveryRoute.DirectP2P;

        var command = new DeleteMessageCommand(
            TargetPayloadId: msgVm.Id,
            ChannelId: _channelId,
            RequestedBy: currentPersona.IdentityId,
            RecipientIdentityId: conversation?.PeerOrGroupId,
            RecipientDeviceId: DeviceId.Primary,
            Route: route);

        await _chatService.DeleteMessageAsync(command).ConfigureAwait(true);
        _reloadCoordinator.TriggerReload();
    }


    [RelayCommand]
    private void CycleDisappearingTimer()
    {
        ExpireAfterSeconds = ExpireAfterSeconds switch
        {
            null => 30,
            30 => 300,
            300 => 3600,
            3600 => 86400,
            86400 => 604800,
            604800 => 2419200,
            _ => null
        };
    }

    [RelayCommand]
    private void RemoveAttachment(string fileName)
    {
        ComposerAttachments.Remove(fileName);
        SaveVolatileDraft();
        OnPropertyChanged(nameof(CanSendMessage));
    }

    private void TickExpirations()
    {
        var nowUtc = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            foreach (var vm in _messagesList)
            {
                vm.Model.UpdateExpiration(nowUtc);
            }
        }
    }

    protected override void DisposeCore()
    {
        SaveVolatileDraft();
        lock (_lock)
        {
            foreach (var vm in _messagesList)
            {
                vm.Dispose();
            }
            _messagesList.Clear();
            _modelsById.Clear();
        }

        base.DisposeCore();
    }
}

