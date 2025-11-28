using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatViewModel : Features.Shell.ViewModelBase
{
    public ReadOnlyObservableCollection<ChatMessage> Messages { get; }
    public BindableReactiveProperty<string> MessageInput { get; }
    public BindableReactiveProperty<bool> CanSend { get; }
    public AsyncRelayCommand SendCommand { get; }

    private readonly ObservableCollection<ChatMessage> _messages = new();
    private string? _sessionId;
    private readonly IChatHistory _history;

    public ChatViewModel(IChatHistory history)
    {
        _history = history;

        MessageInput = new("");
        CanSend = MessageInput.Select(text => !string.IsNullOrWhiteSpace(text)).ToBindableReactiveProperty(false);
        Messages = new ReadOnlyObservableCollection<ChatMessage>(_messages);

        SendCommand = new AsyncRelayCommand(async _ =>
        {
            if (_sessionId is null) return;
            var text = MessageInput.Value;
            if (string.IsNullOrWhiteSpace(text)) return;
            var msg = new ChatMessage { Id = Guid.NewGuid().ToString("N"), Author = "Me", Text = text, TimestampText = DateTime.Now.ToShortTimeString(), IsOwn = true };
            await _history.AppendAsync(_sessionId, msg, CancellationToken.None);
            _messages.Add(msg);
            MessageInput.Value = string.Empty;
        }, _ => CanSend.Value);
    }

    public async void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        _messages.Clear();
        var items = await _history.GetMessagesAsync(sessionId, CancellationToken.None);
        foreach (var m in items) _messages.Add(m);
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(MessageInput, CanSend);
    }
}
