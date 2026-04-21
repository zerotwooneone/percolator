using R3;
using Percolator.Chat.ValueObjects;
using System;

namespace Desktop.Wpf.Features.Chat;

public sealed record ChatMessageSnapshot(
    MessageId Id,
    string Author,
    string Text,
    DateTimeOffset Timestamp,
    bool IsOwn,
    bool IsDelivered,
    bool IsRead,
    bool IsSending = false);

public sealed class ChatMessageModel : IDisposable
{
    private DisposableBag _bag;

    public MessageId Id { get; }
    public string Author { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsOwn { get; }

    public ReactiveProperty<bool> IsDelivered { get; }
    public ReactiveProperty<bool> IsRead { get; }
    public ReactiveProperty<bool> IsSending { get; }

    public ChatMessageModel(ChatMessageSnapshot snapshot)
    {
        Id = snapshot.Id;
        Author = snapshot.Author;
        Text = snapshot.Text;
        Timestamp = snapshot.Timestamp;
        IsOwn = snapshot.IsOwn;

        IsDelivered = new ReactiveProperty<bool>(snapshot.IsDelivered).AddTo(ref _bag);
        IsRead = new ReactiveProperty<bool>(snapshot.IsRead).AddTo(ref _bag);
        IsSending = new ReactiveProperty<bool>(snapshot.IsSending).AddTo(ref _bag);
    }

    public void UpdateFromSnapshot(ChatMessageSnapshot snapshot)
    {
        // Do not let a DB snapshot overwrite a 'true' state with a 'false' state
        if (snapshot.IsDelivered) IsDelivered.Value = true;
        if (snapshot.IsRead) IsRead.Value = true;
        // IsSending is NOT overwritten — managed by optimistic insert + DeliveredReceiptReceivedEventHandler
    }

    public void Dispose() => _bag.Dispose();
}
