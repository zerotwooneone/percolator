using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Wpf.Features.Chat;

public partial class ChatView : UserControl
{
    private INotifyCollectionChanged? _notifier;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += ChatView_DataContextChanged;
    }

    private void ChatView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_notifier is not null)
        {
            _notifier.CollectionChanged -= OnMessagesChanged;
            _notifier = null;
        }

        if (e.NewValue is ChatViewModel vm && vm.Messages is INotifyCollectionChanged ncc)
        {
            _notifier = ncc;
            _notifier.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => MessagesScroll.ScrollToEnd());
    }
}
