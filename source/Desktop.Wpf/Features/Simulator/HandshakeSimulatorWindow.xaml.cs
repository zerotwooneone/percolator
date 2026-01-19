using System.Windows;

namespace Desktop.Wpf.Features.Simulator;

public partial class HandshakeSimulatorWindow : Window
{
    public HandshakeSimulatorWindow(HandshakeSimulatorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    protected override void OnClosed(System.EventArgs e)
    {
        (DataContext as System.IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
