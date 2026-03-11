using System.Windows;

namespace Desktop.Wpf.Features.Sessions;

public partial class ConnectionManagementDialogWindow : Window
{
    public ConnectionManagementDialogWindow()
        : this(new ConnectionManagementDialogViewModel())
    {
    }

    public ConnectionManagementDialogWindow(ConnectionManagementDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
