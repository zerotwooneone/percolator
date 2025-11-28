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

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                // Allow newline
                return;
            }

            // Send on Enter when possible
            if (DataContext is ChatViewModel vm && vm.CanSend.Value)
            {
                vm.SendCommand.Execute(null);
                e.Handled = true; // Prevent newline
            }
        }
    }
}
