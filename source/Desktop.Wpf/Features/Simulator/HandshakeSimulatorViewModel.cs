using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel
{
    private readonly IPendingHandshakeSimulatorService _sim;

    public HandshakeSimulatorViewModel(IPendingHandshakeSimulatorService sim)
    {
        _sim = sim;
        SimulateCommand = new AsyncRelayCommand(ExecuteSimulateAsync, CanExecuteSimulate);
    }

    public string RemotePeerIdText { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? PayloadText { get; set; }

    public string? Status { get; private set; }

    public ICommand SimulateCommand { get; }

    private bool CanExecuteSimulate() => !string.IsNullOrWhiteSpace(RemotePeerIdText);

    private async Task ExecuteSimulateAsync(object? _)
    {
        try
        {
            if (!Guid.TryParse(RemotePeerIdText, out var guid))
            {
                Status = "Invalid PeerId (expecting GUID)";
                return;
            }
            var peer = new PeerId(guid);

            var id = await _sim.AddSyntheticPendingAsync(peer, DisplayName, invitationPayload:null, CancellationToken.None).ConfigureAwait(false);
            Status = $"Added pending handshake: {id.Value}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    private static byte[] ParseHex(string hex)
    {
        if (hex.Length % 2 != 0) throw new FormatException("Hex string must have even length");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<bool>? _canExecute;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter) => await _execute(parameter);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
