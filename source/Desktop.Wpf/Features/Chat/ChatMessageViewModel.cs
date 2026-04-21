using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatMessageViewModel : ViewModelBase
{
    public string Author => _model.Author;
    public string Text => _model.Text;
    public DateTimeOffset Timestamp => _model.Timestamp;
    public bool IsOwn => _model.IsOwn;

    public BindableReactiveProperty<bool> IsDelivered { get; }
    public BindableReactiveProperty<bool> IsRead { get; }
    public BindableReactiveProperty<bool> IsSending { get; }

    private readonly ChatMessageModel _model;
    private DisposableBag _bag;

    public ChatMessageViewModel(ChatMessageModel model, IUiDispatcher ui)
    {
        _model = model;

        // Project reactive properties to UI thread
        IsDelivered = model.IsDelivered
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(model.IsDelivered.Value)
            .AddTo(ref _bag);

        IsRead = model.IsRead
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(model.IsRead.Value)
            .AddTo(ref _bag);

        IsSending = model.IsSending
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(model.IsSending.Value)
            .AddTo(ref _bag);
    }

    protected override void DisposeCore() => _bag.Dispose();
}
