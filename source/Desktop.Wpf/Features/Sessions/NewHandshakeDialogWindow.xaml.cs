using System.Windows;

namespace Desktop.Wpf.Features.Sessions;

public partial class NewHandshakeDialogWindow : Window
{
    public NewHandshakeDialogWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
