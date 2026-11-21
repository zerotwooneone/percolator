// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.ViewModels;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Presentation ViewModel representing an individual chat message bubble in <see cref="ChatView"/> ([INV-DESKTOP-PRESENTATION-MODELS], [VM-001]).
/// </summary>
public sealed partial class ChatMessageViewModel : ViewModelBase
{
    private readonly Func<ChatMessageViewModel, Emoji, Task> _onAddReaction;
    private readonly Func<ChatMessageViewModel, Task> _onDeleteMessage;

    public ChatMessageModel Model { get; }

    public PayloadId Id => Model.Id;
    public ChannelId ChannelId => Model.ChannelId;
    public PublicIdentityId AuthorId => Model.AuthorId;
    public string AuthorDisplayName => Model.AuthorDisplayName;
    public DateTimeOffset TimestampUtc => Model.TimestampUtc;
    public bool IsOutgoing => Model.IsOutgoing;

    public string DisplayContent => Model.DisplayContent;
    public MessageDeliveryStatus DeliveryStatus => Model.DeliveryStatus;
    public bool IsTombstone => Model.IsTombstone;
    public bool IsExpired => Model.IsExpired;
    public string? ExpirationRemainingText => Model.ExpirationRemainingText;
    public IReadOnlyList<ReactionGroupModel> Reactions => Model.Reactions;
    public IReadOnlyList<string> AttachmentFileNames => Model.AttachmentFileNames;
    public bool HasAttachments => Model.AttachmentFileNames.Count > 0;
    public bool HasReactions => Model.Reactions.Count > 0;

    public string FormattedTime => TimestampUtc.ToLocalTime().ToString("t");

    public ChatMessageViewModel(
        ChatMessageModel model,
        Func<ChatMessageViewModel, Emoji, Task> onAddReaction,
        Func<ChatMessageViewModel, Task> onDeleteMessage)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _onAddReaction = onAddReaction ?? throw new ArgumentNullException(nameof(onAddReaction));
        _onDeleteMessage = onDeleteMessage ?? throw new ArgumentNullException(nameof(onDeleteMessage));

        Model.PropertyChanged += OnModelPropertyChanged;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageModel.Content) ||
            e.PropertyName == nameof(ChatMessageModel.DisplayContent) ||
            e.PropertyName == nameof(ChatMessageModel.IsTombstone) ||
            e.PropertyName == nameof(ChatMessageModel.IsExpired))
        {
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(IsTombstone));
            OnPropertyChanged(nameof(IsExpired));
        }
        else if (e.PropertyName == nameof(ChatMessageModel.DeliveryStatus))
        {
            OnPropertyChanged(nameof(DeliveryStatus));
        }
        else if (e.PropertyName == nameof(ChatMessageModel.ExpirationRemainingText))
        {
            OnPropertyChanged(nameof(ExpirationRemainingText));
        }
        else if (e.PropertyName == nameof(ChatMessageModel.Reactions))
        {
            OnPropertyChanged(nameof(Reactions));
            OnPropertyChanged(nameof(HasReactions));
        }
        else if (e.PropertyName == nameof(ChatMessageModel.AttachmentFileNames))
        {
            OnPropertyChanged(nameof(AttachmentFileNames));
            OnPropertyChanged(nameof(HasAttachments));
        }
    }

    [RelayCommand]
    private async Task AddReactionAsync(string emojiText)
    {
        if (!string.IsNullOrWhiteSpace(emojiText))
        {
            var emojiResult = Emoji.Create(emojiText);
            if (emojiResult.IsSuccess)
            {
                await _onAddReaction(this, emojiResult.Value!);
            }
        }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync()
    {
        await _onDeleteMessage(this);
    }

    protected override void DisposeCore()
    {
        Model.PropertyChanged -= OnModelPropertyChanged;
        base.DisposeCore();
    }
}

