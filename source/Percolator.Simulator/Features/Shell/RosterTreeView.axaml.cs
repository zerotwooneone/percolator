using Avalonia.Controls;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Shell;

public partial class RosterTreeView : UserControl
{
    public RosterTreeView()
    {
        InitializeComponent();
    }

    private RosterViewModel? ViewModel => DataContext as RosterViewModel;

    private void OnSelectPrimaryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SimulatedNodeSummary node })
        {
            ViewModel?.SelectPrimaryCommand.Execute(node);
        }
    }

    private void OnSelectSecondaryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SimulatedNodeSummary node })
        {
            ViewModel?.SelectSecondaryCommand.Execute(node);
        }
    }

    private void OnToggleMuteClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SimulatedNodeSummary node })
        {
            ViewModel?.ToggleMuteCommand.Execute(node);
        }
    }

    private void OnTogglePinClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SimulatedNodeSummary node })
        {
            ViewModel?.TogglePinCommand.Execute(node);
        }
    }
}
