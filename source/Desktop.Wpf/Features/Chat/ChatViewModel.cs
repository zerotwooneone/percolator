using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Mvvm;
using Desktop.Wpf.Features.Sessions;

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatViewModel : Features.Shell.ViewModelBase
{
    public ReadOnlyObservableCollection<ChatMessage> Messages { get; }
    public BindableReactiveProperty<string> MessageInput { get; }
    public BindableReactiveProperty<bool> CanSend { get; }
    public BindableReactiveProperty<string> Title { get; }
    public BindableReactiveProperty<string> Initials { get; }
    public AsyncRelayCommand SendCommand { get; }

    private readonly ObservableCollection<ChatMessage> _messages = new();
    private string? _sessionId;
    private readonly IChatHistory _history;
    private readonly ISessionDirectory _sessions;

    public ChatViewModel(IChatHistory history, ISessionDirectory sessions)
    {
        _history = history;
        _sessions = sessions;

        MessageInput = new("");
        CanSend = MessageInput.Select(text => !string.IsNullOrWhiteSpace(text)).ToBindableReactiveProperty(false);
        Messages = new ReadOnlyObservableCollection<ChatMessage>(_messages);
        Title = new BindableReactiveProperty<string>("Secure Chat");
        Initials = new BindableReactiveProperty<string>("?");
        Title.Subscribe(t => Initials.Value = ComputeInitials(t));

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

        // Propagate CanSend changes to the command so the button updates
        CanSend.Subscribe(_ => SendCommand.RaiseCanExecuteChanged());
    }

    public async void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        _messages.Clear();
        var items = await _history.GetMessagesAsync(sessionId, CancellationToken.None);
        foreach (var m in items) _messages.Add(m);
        try
        {
            var all = await _sessions.GetAllAsync(CancellationToken.None);
            var name = all.FirstOrDefault(x => x.Id == sessionId)?.DisplayName;
            if (!string.IsNullOrWhiteSpace(name)) Title.Value = name!;
        }
        catch { }
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(MessageInput, CanSend, Title, Initials);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
