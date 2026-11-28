// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.ViewModels;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
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
    private readonly IFileTransferActionHandler _fileTransferActionHandler;
    private readonly IFileTransferQueryService _fileTransferQueryService;
    private readonly IDialogService _dialogService;
    private readonly IFilePickerService _filePickerService;
    private readonly IPeerIntroductionConsentRepository _peerIntroductionConsentRepository;

    private readonly ObservableList<ChatMessageViewModel> _messagesList = new();
    private readonly Dictionary<PayloadId, ChatMessageModel> _modelsById = new();
    private readonly ChatReloadCoordinator _reloadCoordinator;
    private readonly object _lock = new();

    private readonly ObservableList<FileTransferItemViewModel> _transfersList = new();
    private readonly object _transfersLock = new();

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

    [ObservableProperty]
    private bool _hasActiveTransfers;

    [ObservableProperty]
    private bool _is1To1Conversation;

    [ObservableProperty]
    private bool _isIntroduceFlyoutOpen;

    [ObservableProperty]
    private ConversationSummaryModel? _selectedCandidateForIntro;

    [ObservableProperty]
    private string _introNoteText = string.Empty;

    [ObservableProperty]
    private bool _hasPendingIntroduction;

    [ObservableProperty]
    private string _pendingIntroductionText = string.Empty;

    [ObservableProperty]
    private string? _pendingIntroductionRequestId;

    public INotifyCollectionChangedSynchronizedViewList<ChatMessageViewModel> Messages { get; }
    public INotifyCollectionChangedSynchronizedViewList<FileTransferItemViewModel> Transfers { get; }
    public ObservableList<ConversationSummaryModel> AvailableCandidatesForIntro { get; } = new();

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
        ILogger<ChatReloadCoordinator> reloadCoordinatorLogger,
        IFileTransferActionHandler fileTransferActionHandler,
        IFileTransferQueryService fileTransferQueryService,
        IDialogService dialogService,
        IFilePickerService filePickerService,
        IPeerIntroductionConsentRepository peerIntroductionConsentRepository)
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

        _fileTransferActionHandler = fileTransferActionHandler ?? throw new ArgumentNullException(nameof(fileTransferActionHandler));
        _fileTransferQueryService = fileTransferQueryService ?? throw new ArgumentNullException(nameof(fileTransferQueryService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
        _peerIntroductionConsentRepository = peerIntroductionConsentRepository ?? throw new ArgumentNullException(nameof(peerIntroductionConsentRepository));

        var view = _messagesList.CreateView(x => x);
        Messages = SynchronizationContext.Current != null
            ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            : view.ToNotifyCollectionChanged();
        Messages.AddTo(ref Bag);

        var transfersView = _transfersList.CreateView(x => x);
        Transfers = SynchronizationContext.Current != null
            ? transfersView.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            : transfersView.ToNotifyCollectionChanged();
        Transfers.AddTo(ref Bag);

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

        // Vault lock / persona switch listener: teardown active transfers
        _currentPersonaService.CurrentPersonaChanged
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(p =>
            {
                if (p == null || !p.IdentityId.IsValid)
                {
                    TeardownTransfersOnLock();
                }
            })
            .AddTo(ref Bag);

        UpdateConversationMetadata();
        RestoreVolatileDraft();

        // Expiring message countdown timer & transfer polling: ticks every 1 second
        Observable.Interval(TimeSpan.FromSeconds(1), _timeProvider)
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => OnPeriodicTimerTick())
            .AddTo(ref Bag);

        // Initial message and transfer load
        _ = LoadInitialMessagesAsync();
        _ = RefreshTransfersAndPendingIntroductionsAsync();
    }

    private void OnPeriodicTimerTick()
    {
        TickExpirations();
        _ = RefreshTransfersAndPendingIntroductionsAsync();
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
            Is1To1Conversation = match.RouteType != ConversationRouteType.Relayed || match.RelayId == null;
        }

        // Update available candidate contacts for introduction
        AvailableCandidatesForIntro.Clear();
        foreach (var conv in _conversationStateService.Conversations)
        {
            if (conv.ConversationId != _channelId &&
                conv.PeerOrGroupId.IsValid &&
                (match == null || conv.PeerOrGroupId != match.PeerOrGroupId))
            {
                AvailableCandidatesForIntro.Add(conv);
            }
        }
        if (SelectedCandidateForIntro == null && AvailableCandidatesForIntro.Count > 0)
        {
            SelectedCandidateForIntro = AvailableCandidatesForIntro[0];
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

        var route = DeliveryRoute.RelayedOneToOne;

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
        var route = DeliveryRoute.RelayedOneToOne;

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
        var route = DeliveryRoute.RelayedOneToOne;

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

    #region File Transfer Operations

    [RelayCommand]
    public async Task SendFileAsync()
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid)
        {
            _logger.LogWarning("Cannot initiate file transfer: no active persona.");
            return;
        }

        var match = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
        var contactName = match?.DisplayName ?? "Peer";

        // Unavoidable IP-disclosure notice
        var disclosureResult = await _dialogService.ShowAsync(new DialogRequest(
            Title: "Direct P2P File Transfer IP Disclosure",
            Message: $"Direct P2P file transfer connects directly to {contactName} (and group swarm peers) without relaying through the Exchange. This reveals your IP address to participating peers.",
            PrimaryButtonText: "I Understand & Connect Directly",
            CloseButtonText: "Cancel")).ConfigureAwait(true);

        if (disclosureResult != DialogResult.Primary)
        {
            _logger.LogInformation("File transfer cancelled by user at IP disclosure notice.");
            return;
        }

        var selectedFiles = await _filePickerService.PickFilesAsync("Select file(s) to transfer", allowMultiple: true).ConfigureAwait(true);
        if (selectedFiles == null || selectedFiles.Count == 0) return;

        var firstFile = selectedFiles[0];
        var directory = Path.GetDirectoryName(firstFile);
        if (string.IsNullOrEmpty(directory)) return;

        var rootPath = RootDirectoryPath.Parse(directory);
        var relativePaths = selectedFiles
            .Select(f => SanitizedRelativePath.Parse(Path.GetRelativePath(directory, f)))
            .ToList();

        var recipients = match != null && match.PeerOrGroupId.IsValid
            ? new[] { match.PeerOrGroupId }
            : Array.Empty<PublicIdentityId>();

        var bundleName = RootBundleName.Parse(Path.GetFileName(firstFile));

        var result = await _fileTransferActionHandler.InitiateTransferAsync(
            channelId: _channelId,
            localInitiator: currentPersona.IdentityId,
            recipients: recipients,
            sourceRoot: rootPath,
            filePaths: relativePaths,
            wanPolicy: DirectWanPolicy.ExplicitlyAllowed,
            customBundleName: bundleName).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            await RefreshTransfersAsync().ConfigureAwait(true);
        }
        else
        {
            _logger.LogWarning("Failed to initiate transfer: {Error}", result.Error.Description);
        }
    }

    public async Task AcceptTransferAsync(TransferSessionId sessionId)
    {
        var match = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
        var contactName = match?.DisplayName ?? "Peer";

        // Unavoidable IP-disclosure notice
        var disclosureResult = await _dialogService.ShowAsync(new DialogRequest(
            Title: "Direct P2P File Transfer IP Disclosure",
            Message: $"Direct P2P file transfer connects directly to {contactName} (and group swarm peers) without relaying through the Exchange. This reveals your IP address to participating peers.",
            PrimaryButtonText: "I Understand & Connect Directly",
            CloseButtonText: "Cancel")).ConfigureAwait(true);

        if (disclosureResult != DialogResult.Primary)
        {
            _logger.LogInformation("Transfer acceptance cancelled at IP disclosure notice.");
            return;
        }

        var destinationFolder = await _filePickerService.PickFolderAsync("Select Destination Folder").ConfigureAwait(true);
        if (string.IsNullOrEmpty(destinationFolder)) return;

        var result = await _fileTransferActionHandler.AcceptTransferAsync(
            sessionId,
            RootDirectoryPath.Parse(destinationFolder)).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            await RefreshTransfersAsync().ConfigureAwait(true);
        }
        else
        {
            _logger.LogWarning("Failed to accept transfer: {Error}", result.Error.Description);
        }
    }

    public async Task PauseTransferAsync(TransferSessionId sessionId)
    {
        await _fileTransferActionHandler.PauseTransferAsync(sessionId).ConfigureAwait(true);
        await RefreshTransfersAsync().ConfigureAwait(true);
    }

    public async Task ResumeTransferAsync(TransferSessionId sessionId)
    {
        await _fileTransferActionHandler.ResumeTransferAsync(sessionId).ConfigureAwait(true);
        await RefreshTransfersAsync().ConfigureAwait(true);
    }

    public async Task StopSeedingAsync(TransferSessionId sessionId)
    {
        await _fileTransferActionHandler.StopSeedingAsync(sessionId).ConfigureAwait(true);
        await RefreshTransfersAsync().ConfigureAwait(true);
    }

    public async Task CancelTransferAsync(TransferSessionId sessionId)
    {
        await _fileTransferActionHandler.CancelTransferAsync(sessionId, TransferTerminationReason.SenderCancelled).ConfigureAwait(true);
        await RefreshTransfersAsync().ConfigureAwait(true);
    }

    public async Task RefreshTransfersAndPendingIntroductionsAsync()
    {
        await RefreshTransfersAsync().ConfigureAwait(true);
        await RefreshPendingIntroductionsAsync().ConfigureAwait(true);
    }

    public async Task RefreshTransfersAsync()
    {
        try
        {
            var match = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
            var recipientId = match?.PeerOrGroupId;

            var all = await _fileTransferQueryService.GetAllTransfersAsync().ConfigureAwait(true);
            var relevant = all.Where(t =>
                (t.ChannelId.HasValue && t.ChannelId.Value == _channelId) ||
                (!t.ChannelId.HasValue && recipientId.HasValue && recipientId.Value.IsValid && t.InitiatorId == recipientId.Value)).ToList();

            lock (_transfersLock)
            {
                var existingById = _transfersList.ToDictionary(t => t.SessionId);
                var activeIds = new HashSet<TransferSessionId>();

                foreach (var model in relevant)
                {
                    activeIds.Add(model.SessionId);
                    if (existingById.TryGetValue(model.SessionId, out var existingItem))
                    {
                        existingItem.UpdateState(
                            model.DownloadedBytes.Bytes,
                            model.TotalBytes.Bytes,
                            model.Status,
                            model.DownloadSpeed.BytesPerSecond,
                            model.ConnectedPeers.Count,
                            model.ConnectedStreams.Count);
                    }
                    else
                    {
                        var newItem = new FileTransferItemViewModel(
                            model.SessionId,
                            model.RootName.Value,
                            model.TotalBytes.Bytes,
                            model.DownloadedBytes.Bytes,
                            model.Status,
                            model.DownloadSpeed.BytesPerSecond,
                            model.ConnectedPeers.Count,
                            model.ConnectedStreams.Count,
                            onAccept: AcceptTransferAsync,
                            onCancel: CancelTransferAsync,
                            onPause: PauseTransferAsync,
                            onResume: ResumeTransferAsync,
                            onStopSeeding: StopSeedingAsync);

                        _transfersList.Add(newItem);
                    }
                }

                for (int i = _transfersList.Count - 1; i >= 0; i--)
                {
                    if (!activeIds.Contains(_transfersList[i].SessionId))
                    {
                        _transfersList[i].Dispose();
                        _transfersList.RemoveAt(i);
                    }
                }

                HasActiveTransfers = _transfersList.Count > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing file transfers for channel {ChannelId}", _channelId);
        }
    }

    private void TeardownTransfersOnLock()
    {
        lock (_transfersLock)
        {
            foreach (var transfer in _transfersList)
            {
                if (transfer.IsInFlight || transfer.IsSeeding)
                {
                    var sessionId = transfer.SessionId;
                    _fileTransferActionHandler.CancelTransferAsync(sessionId, TransferTerminationReason.SenderCancelled)
                        .AsTask()
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted && t.Exception != null)
                            {
                                _logger.LogError(t.Exception, "Failed to cancel transfer {SessionId} on vault lock teardown.", sessionId);
                            }
                        }, TaskScheduler.Default);
                }
            }
        }
    }

    #endregion

    #region Introduction Operations

    [RelayCommand]
    public async Task RequestIntroductionConsentAsync()
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid || SelectedCandidateForIntro == null) return;

        var cmd = new RequestIntroductionConsentCommand(
            ChannelId: _channelId,
            RequesterId: currentPersona.IdentityId,
            CandidatePeerId: SelectedCandidateForIntro.PeerOrGroupId,
            CandidateDisplayName: SelectedCandidateForIntro.DisplayName,
            ForwardChannelId: SelectedCandidateForIntro.ConversationId,
            Note: string.IsNullOrWhiteSpace(IntroNoteText) ? null : IntroNoteText.Trim());

        var result = await _chatService.RequestIntroductionConsentAsync(cmd).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IntroNoteText = string.Empty;
            IsIntroduceFlyoutOpen = false;
        }
        else
        {
            _logger.LogWarning("Failed to request introduction consent: {Error}", result.Error.Description);
        }
    }

    [RelayCommand]
    public async Task AllowPendingIntroductionAsync()
    {
        if (string.IsNullOrEmpty(PendingIntroductionRequestId)) return;

        var cmd = new RespondToIntroductionConsentCommand(PendingIntroductionRequestId, Allowed: true);
        var result = await _chatService.RespondToIntroductionConsentAsync(cmd).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            HasPendingIntroduction = false;
            PendingIntroductionRequestId = null;
            PendingIntroductionText = string.Empty;
        }
    }

    [RelayCommand]
    public async Task DeclinePendingIntroductionAsync()
    {
        if (string.IsNullOrEmpty(PendingIntroductionRequestId)) return;

        var cmd = new RespondToIntroductionConsentCommand(PendingIntroductionRequestId, Allowed: false, DeclineReason: "Declined by user.");
        var result = await _chatService.RespondToIntroductionConsentAsync(cmd).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            HasPendingIntroduction = false;
            PendingIntroductionRequestId = null;
            PendingIntroductionText = string.Empty;
        }
    }

    public async Task RefreshPendingIntroductionsAsync()
    {
        var currentPersona = _currentPersonaService.CurrentPersona;
        if (currentPersona == null || !currentPersona.IdentityId.IsValid) return;

        try
        {
            var match = _conversationStateService.Conversations.FirstOrDefault(c => c.ConversationId == _channelId);
            var recipientId = match?.PeerOrGroupId;

            var pending = await _peerIntroductionConsentRepository.GetPendingInboundRequestsAsync(currentPersona.IdentityId).ConfigureAwait(true);
            var matching = pending.FirstOrDefault(p =>
                p.ChannelId == _channelId ||
                (recipientId.HasValue && recipientId.Value.IsValid && p.RemotePeerId == recipientId.Value));

            if (matching != null)
            {
                HasPendingIntroduction = true;
                PendingIntroductionRequestId = matching.RequestId;
                var noteSuffix = string.IsNullOrWhiteSpace(matching.Note) ? "" : $" Note: \"{matching.Note}\"";
                PendingIntroductionText = $"🤝 {ConversationTitle} wants to introduce {matching.CandidateDisplayName} to you.{noteSuffix}";
            }
            else
            {
                HasPendingIntroduction = false;
                PendingIntroductionRequestId = null;
                PendingIntroductionText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing pending introductions for channel {ChannelId}", _channelId);
        }
    }

    #endregion

    protected override void DisposeCore()
    {
        SaveVolatileDraft();
        TeardownTransfersOnLock();

        lock (_lock)
        {
            foreach (var vm in _messagesList)
            {
                vm.Dispose();
            }
            _messagesList.Clear();
            _modelsById.Clear();
        }

        lock (_transfersLock)
        {
            foreach (var t in _transfersList)
            {
                t.Dispose();
            }
            _transfersList.Clear();
        }

        base.DisposeCore();
    }
}
