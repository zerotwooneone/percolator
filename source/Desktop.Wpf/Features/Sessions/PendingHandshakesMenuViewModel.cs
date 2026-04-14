using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Shared.Mvvm;
using Desktop.Wpf.Shared.Windowing;
using MediatR;
using ObservableCollections;
using Percolator.Application.Network;
using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakesMenuViewModel : System.IDisposable
{
    public NotifyCollectionChangedSynchronizedViewList<PendingHandshakeItemViewModel> PendingHandshakes { get; }

    public AsyncRelayCommand AcceptHandshakeCommand { get; }
    public AsyncRelayCommand BurnHandshakeCommand { get; }
    public AsyncRelayCommand OpenNewHandshakeCommand { get; }

    private readonly IMediator _mediator;
    private readonly PeerConnectionStateService _stateService;
    private readonly DisposableBag _bag;
    private readonly ISynchronizedView<PeerPendingInvitationModel, PendingHandshakeItemViewModel> _pendingView;

    public PendingHandshakesMenuViewModel(
        IWindowManager windowManager,
        IMediator mediator,
        PeerConnectionStateService stateService,
        IUiDispatcher ui)
    {
        _mediator = mediator;
        _stateService = stateService;
        _bag = new DisposableBag();

        OpenNewHandshakeCommand = new AsyncRelayCommand(_ =>
        {
            windowManager.ShowFor<ConnectionManagementDialogViewModel>();
            return Task.CompletedTask;
        });

        AcceptHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItemViewModel item)
            {
                var result = await _mediator.Send(new ApprovePendingSessionCommand(item.PendingId)).ConfigureAwait(false);
                switch (result)
                {
                    case ApprovePendingSessionResult.Accepted accepted:
                        item.StatusText.Value = "Accepted";
                        item.SendPath = accepted.SendPath;
                        item.RequestCorrelationId = accepted.RequestCorrelationId.Value.ToString();
                        item.IsExpired.Value = false;
                        break;
                    case ApprovePendingSessionResult.RejectedNotReady:
                        item.StatusText.Value = "Rejected: Not Ready";
                        break;
                    case ApprovePendingSessionResult.RejectedInvalid:
                        item.StatusText.Value = "Rejected: Invalid";
                        break;
                    case ApprovePendingSessionResult.RejectedExpired:
                        item.StatusText.Value = "Rejected: Expired";
                        item.IsExpired.Value = true;
                        break;
                    case ApprovePendingSessionResult.Failed failed:
                        item.StatusText.Value = $"Failed: {failed.ErrorMessage}";
                        break;
                    default:
                        item.StatusText.Value = "Failed: Unknown";
                        break;
                }
            }
        });
        BurnHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItemViewModel item)
            {
                await _mediator.Send(new RejectPendingSessionCommand(item.PendingId)).ConfigureAwait(false);
            }
        });

        // Create synchronized view of pending inbound
        _pendingView = _stateService.PendingInbound
            .CreateView(model => new PendingHandshakeItemViewModel(
                model.PeerName.CurrentValue,
                ComputeInitials(model.PeerName.CurrentValue),
                "bundle text",
                new PendingSessionId(model.PendingSessionId),
                model.IsRelayed.CurrentValue,
                null))
            .AddTo(ref _bag);

        PendingHandshakes = _pendingView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

    public void Dispose()
    {
        _bag.Dispose();
    }
}
