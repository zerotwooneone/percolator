using System.Windows;

namespace Desktop.Wpf.Features.Sessions;

public partial class ConnectionManagementDialogWindow : Window
{
    public ConnectionManagementDialogWindow(ConnectionManagementDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
