using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Application.Ingress;

namespace Desktop.Wpf.Features.Simulator;

public sealed class HandshakeSimulatorViewModel
{
    private readonly IPendingHandshakeSimulatorService _sim;

    public HandshakeSimulatorViewModel(IPendingHandshakeSimulatorService sim)
    {
        _sim = sim;
        SimulateCommand = new AsyncRelayCommand(ExecuteSimulateAsync);
    }

    public string? DisplayName { get; set; }

    public string? Status { get; private set; }

    public ICommand SimulateCommand { get; }

    private async Task ExecuteSimulateAsync(object? _)
    {
        try
        {
            var result = await _sim.AddSyntheticPendingAsync(DisplayName, invitationPayload: null, CancellationToken.None).ConfigureAwait(false);

            Status = result.Status switch
            {
                PendingHandshakeIngressStatus.Accepted => $"Accepted: {result.PendingSessionId!.Value}",
                PendingHandshakeIngressStatus.RejectedNotReady => result.NotUntil is null
                    ? "Rejected: not ready"
                    : $"Rejected: not ready until {result.NotUntil:O}",
                PendingHandshakeIngressStatus.RejectedInvalid => $"Rejected: invalid ({result.ErrorMessage})",
                PendingHandshakeIngressStatus.Failed => $"Failed: {result.ErrorMessage}",
                _ => "Unknown result"
            };
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }
}

