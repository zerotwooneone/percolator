using System.Windows.Controls;

namespace Desktop.Wpf.Features.Sessions;

public partial class SessionsSidebarView : UserControl
{
    public SessionsSidebarView(SessionsSidebarViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
