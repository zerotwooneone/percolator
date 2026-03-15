using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Desktop.Wpf.Shared.Mvvm;
using Desktop.Wpf.Shared.Windowing;
using MediatR;
using Percolator.Application.Network;
using Desktop.Wpf.Features.Sessions.State;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeItem
{
    public required string DisplayName { get; init; } 
    public required string Initials { get; set; }
    public string BundleText { get; set; } = "Not Set";
    public required PendingSessionId PendingId { get; init; }

    public string StatusText { get; set; } = "Pending";
    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }
    public bool IsExpired { get; set; }

    public bool IsRelayed { get; set; }
    public string? RelayInfoText { get; set; }
}

public sealed class PendingHandshakesMenuViewModel : System.IDisposable
{
    public ObservableCollection<PendingHandshakeItem> PendingHandshakes { get; } = new();

    public AsyncRelayCommand AcceptHandshakeCommand { get; }
    public AsyncRelayCommand BurnHandshakeCommand { get; }
    public AsyncRelayCommand OpenNewHandshakeCommand { get; }

    private readonly IMediator _mediator;
    private readonly ISecureChannelsStore _store;

    public PendingHandshakesMenuViewModel(
        IWindowManager windowManager,
        IMediator mediator,
        ISecureChannelsStore store)
    {
        _mediator = mediator;
        _store = store;

        OpenNewHandshakeCommand = new AsyncRelayCommand(_ =>
        {
            windowManager.ShowFor<ConnectionManagementDialogViewModel>();
            return Task.CompletedTask;
        });

        AcceptHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                var result = await _mediator.Send(new ApprovePendingSessionCommand(item.PendingId)).ConfigureAwait(false);
                switch (result)
                {
                    case ApprovePendingSessionResult.Accepted accepted:
                        item.StatusText = "Accepted";
                        item.SendPath = accepted.SendPath;
                        item.RequestCorrelationId = accepted.RequestCorrelationId.Value.ToString();
                        item.IsExpired = false;
                        break;
                    case ApprovePendingSessionResult.RejectedNotReady:
                        item.StatusText = "Rejected: Not Ready";
                        break;
                    case ApprovePendingSessionResult.RejectedInvalid:
                        item.StatusText = "Rejected: Invalid";
                        break;
                    case ApprovePendingSessionResult.RejectedExpired:
                        item.StatusText = "Rejected: Expired";
                        item.IsExpired = true;
                        break;
                    case ApprovePendingSessionResult.Failed failed:
                        item.StatusText = $"Failed: {failed.ErrorMessage}";
                        break;
                    default:
                        item.StatusText = "Failed: Unknown";
                        break;
                }
            }
        });
        BurnHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                await _mediator.Send(new RejectPendingSessionCommand(item.PendingId)).ConfigureAwait(false);
            }
        });

        ((INotifyCollectionChanged)_store.PendingInbound).CollectionChanged += OnPendingInboundChanged;

        RebuildFromStore();
    }

    private void OnPendingInboundChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildFromStore();

    private void RebuildFromStore()
    {
        var snapshot = _store.PendingInbound
            .Select(m => new PendingHandshakeItem
            {
                DisplayName = m.DisplayNameCurrent,
                Initials = m.InitialsCurrent,
                BundleText = "bundle text",
                PendingId = new Percolator.Cryptography.PendingSessionId(m.PendingSessionId),
                IsRelayed = m.IsRelayedCurrent,
                RelayInfoText = null
            })
            .ToList();

        PendingHandshakes.Clear();
        foreach (var it in snapshot)
        {
            PendingHandshakes.Add(it);
        }
    }

    public void Dispose()
        => ((INotifyCollectionChanged)_store.PendingInbound).CollectionChanged -= OnPendingInboundChanged;
}
