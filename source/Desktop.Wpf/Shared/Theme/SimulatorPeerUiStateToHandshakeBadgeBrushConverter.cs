using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Desktop.Wpf.Features.Simulator;

namespace Desktop.Wpf.Shared.Theme;

public sealed class SimulatorPeerUiStateToHandshakeBadgeBrushConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SimulatorPeerUiState state)
        {
            return Brushes.Transparent;
        }

        var key = state switch
        {
            SimulatorPeerUiState.Ready => "Simulator.HandshakeBadge.ReadyBrush",
            SimulatorPeerUiState.OutboundPending => "Simulator.HandshakeBadge.OutboundPendingBrush",
            SimulatorPeerUiState.InboundPending => "Simulator.HandshakeBadge.InboundPendingBrush",
            SimulatorPeerUiState.Established => "Simulator.HandshakeBadge.EstablishedBrush",
            SimulatorPeerUiState.Expired => "Simulator.HandshakeBadge.ExpiredBrush",
            _ => "Simulator.HandshakeBadge.ReadyBrush"
        };

        if (Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
