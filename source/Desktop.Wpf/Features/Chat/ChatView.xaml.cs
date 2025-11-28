using System.Windows.Controls;

namespace Desktop.Wpf.Features.Chat;

public partial class ChatView : UserControl
{
    public ChatView(ChatViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
