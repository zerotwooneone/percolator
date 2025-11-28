using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Wpf.Features.Chat;

public partial class ChatView : UserControl
{
    public ChatView(ChatViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        if (vm.Messages is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, __) =>
            {
                // Defer to layout pass then scroll
                Dispatcher.InvokeAsync(() => MessagesScroll.ScrollToEnd());
            };
        }
    }
}
