using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed record HeldEnvelopeItemViewModel(
    string QueueId,
    int PayloadBytes,
    string EnqueuedText)
{
    public string DisplayText => $"Envelope [{QueueId[..Math.Min(8, QueueId.Length)]}...]: {PayloadBytes} bytes at {EnqueuedText}";
}

internal sealed partial class RelayCardViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus? _stateBus;
    private readonly ISimulatedRelayHost? _host;
    private readonly ISimulatorLogSink? _logSink;

    public SimulatedNodeId NodeId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _boundEndpoint;

    [ObservableProperty]
    private int _heldEnvelopeCount = 0;

    [ObservableProperty]
    private int _hostedOpkCount = 0;

    public ObservableCollection<HeldEnvelopeItemViewModel> HeldEnvelopes { get; } = new();

    public RelayCardViewModel(
        SimulatedNodeSummary summary,
        IStatusToastService toastService,
        ISimulatorStateBus? stateBus = null,
        ISimulatedRelayHost? host = null,
        ISimulatorLogSink? logSink = null)
    {
        NodeId = summary.Id;
        _displayName = summary.DisplayName;
        _boundEndpoint = summary.Subtitle;
        _toastService = toastService;
        _stateBus = stateBus;
        _host = host;
        _logSink = logSink;

        if (_host != null)
        {
            _heldEnvelopeCount = _host.TotalHeldEnvelopeCount;
            _hostedOpkCount = _host.TotalHostedOpkCount;
            RefreshHeldEnvelopes();
        }

        if (_stateBus != null)
        {
            _stateBus.Signals
                .Where(s => s.NodeId == NodeId && s.Category == SignalCategory.RelayQueueChanged)
                .Subscribe(_ =>
                {
                    UiTaskRunner.PostToUi(() =>
                    {
                        if (_host != null)
                        {
                            HeldEnvelopeCount = _host.TotalHeldEnvelopeCount;
                            RefreshHeldEnvelopes();
                        }
                    });
                })
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => s.NodeId == NodeId && s.Category == SignalCategory.HostedPreKeyCountChanged)
                .Subscribe(_ =>
                {
                    UiTaskRunner.PostToUi(() =>
                    {
                        if (_host != null)
                        {
                            HostedOpkCount = _host.TotalHostedOpkCount;
                        }
                    });
                })
                .AddTo(ref Disposables);
        }
    }

    private void RefreshHeldEnvelopes()
    {
        HeldEnvelopes.Clear();
        if (_host == null) return;

        var envelopes = _host.ChaosController.GetHeldEnvelopes();
        foreach (var env in envelopes)
        {
            var timeText = env.EnqueuedAtUtc != null
                ? env.EnqueuedAtUtc.ToDateTimeOffset().ToLocalTime().ToString("HH:mm:ss")
                : "N/A";
            HeldEnvelopes.Add(new HeldEnvelopeItemViewModel(
                env.QueueId,
                env.EnvelopePayload.Length,
                timeText));
        }
    }

    [RelayCommand]
    private void DuplicateEnvelope()
    {
        if (_host == null)
        {
            _toastService.ShowToast("Duplicate Failed", "Relay host is not active.", ToastSeverity.Warning);
            return;
        }

        var success = _host.ChaosController.DuplicateEnvelope();
        if (success)
        {
            _toastService.ShowToast("Queue Chaos", "Duplicated an envelope in mailbox queue.", ToastSeverity.Informational);
            _logSink?.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                DisplayName,
                NodeId,
                "QueueChaos",
                "Chaos mutation executed: [ 👯 Duplicate ] envelope cloned. Replay attack verification ready."));
        }
        else
        {
            _toastService.ShowToast("Queue Empty", "No envelopes in queue to duplicate.", ToastSeverity.Warning);
        }
    }

    [RelayCommand]
    private void FlipBit()
    {
        if (_host == null)
        {
            _toastService.ShowToast("Flip Bit Failed", "Relay host is not active.", ToastSeverity.Warning);
            return;
        }

        var success = _host.ChaosController.FlipBit(byteOffset: 10);
        if (success)
        {
            _toastService.ShowToast("Queue Chaos", "Flipped 1 bit in envelope payload.", ToastSeverity.Informational);
            _logSink?.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                DisplayName,
                NodeId,
                "QueueChaos",
                "Chaos mutation executed: [ ⚡ Flip 1 Bit ] bit flipped at offset 10. AEAD tamper verification ready."));
        }
        else
        {
            _toastService.ShowToast("Queue Empty", "No envelopes in queue to corrupt.", ToastSeverity.Warning);
        }
    }

    [RelayCommand]
    private void ShuffleQueue()
    {
        if (_host == null)
        {
            _toastService.ShowToast("Shuffle Failed", "Relay host is not active.", ToastSeverity.Warning);
            return;
        }

        var success = _host.ChaosController.ShuffleQueue();
        if (success)
        {
            _toastService.ShowToast("Queue Chaos", "Shuffled queue order.", ToastSeverity.Informational);
            _logSink?.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                DisplayName,
                NodeId,
                "QueueChaos",
                "Chaos mutation executed: [ 🔀 Shuffle Queue Order ] envelope order randomized."));
        }
        else
        {
            _toastService.ShowToast("Cannot Shuffle", "Need at least 2 envelopes in mailbox to shuffle.", ToastSeverity.Warning);
        }
    }
}
