using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel
{
    private readonly IPendingHandshakeSimulatorService _sim;

    public HandshakeSimulatorViewModel(IPendingHandshakeSimulatorService sim)
    {
        _sim = sim;
        SimulateCommand = new AsyncRelayCommand(ExecuteSimulateAsync);
        SimulateManyCommand = new AsyncRelayCommand(ExecuteSimulateManyAsync);
        CorrelateOutboundCommand = new AsyncRelayCommand(ExecuteCorrelateOutboundAsync);
    }

    public string? DisplayName { get; set; }

    public string? Status { get; private set; }

    public int PeerCount { get; set; } = 1;

    public IReadOnlyList<SimulatedPeerSnapshot> Peers { get; private set; } = Array.Empty<SimulatedPeerSnapshot>();

    public ICommand SimulateCommand { get; }
    public ICommand SimulateManyCommand { get; }
    public ICommand CorrelateOutboundCommand { get; }

    private async Task ExecuteSimulateAsync(object? _)
    {
        try
        {
            var correlation = await _sim.AddSyntheticPendingAsync(DisplayName, CancellationToken.None).ConfigureAwait(false);

            Peers = _sim.SnapshotPeers();

            Status = $"Created pending invite {correlation.Value}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    private async Task ExecuteSimulateManyAsync(object? _)
    {
        try
        {
            var count = PeerCount <= 0 ? 0 : PeerCount;
            var created = await _sim.AddSyntheticPendingsAsync(count, CancellationToken.None).ConfigureAwait(false);
            Peers = _sim.SnapshotPeers();
            Status = $"Created {created.Count} pending";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    private async Task ExecuteCorrelateOutboundAsync(object? _)
    {
        try
        {
            var advanced = _sim.CorrelateOutboundSnapshot();
            Peers = _sim.SnapshotPeers();
            Status = $"Correlated {advanced} outbound";
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }
}

