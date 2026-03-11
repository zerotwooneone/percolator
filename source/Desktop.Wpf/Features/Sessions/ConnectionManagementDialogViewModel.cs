using Desktop.Wpf.Shared.Mvvm;
using R3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingInvitationItem
{
    public required Guid PendingSessionId { get; init; }
    public required string DisplayName { get; init; }
    public required string Initials { get; init; }
    public required bool IsRelayed { get; init; }
    public string? RelayInfoText { get; init; }

    public string StatusText { get; set; } = "Pending";
    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }
    public bool IsExpired { get; set; }
}

public sealed class ConnectionManagementDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;

    private readonly IMainInvitationInbox _inbox;
    private readonly IMainInvitationActions _actions;
    private readonly IMainInvitationInboxEvents _inboxEvents;

    private readonly ObservableCollection<PendingInvitationItem> _pendingInvitations = new();

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public ReadOnlyObservableCollection<PendingInvitationItem> PendingInvitations { get; }

    public AsyncRelayCommand AcceptInvitationCommand { get; }
    public AsyncRelayCommand BurnInvitationCommand { get; }
    public AsyncRelayCommand RefreshInboxCommand { get; }

    public ConnectionManagementDialogViewModel(
        IMainInvitationInbox inbox,
        IMainInvitationActions actions,
        IMainInvitationInboxEvents inboxEvents)
    {
        _inbox = inbox;
        _actions = actions;
        _inboxEvents = inboxEvents;
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        PendingInvitations = new ReadOnlyObservableCollection<PendingInvitationItem>(_pendingInvitations);

        RefreshInboxCommand = new AsyncRelayCommand(async _ => await RefreshInboxAsync().ConfigureAwait(false));
        AcceptInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteAcceptAsync(obj).ConfigureAwait(false));
        BurnInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteBurnAsync(obj).ConfigureAwait(false));

        _inboxEvents.Changed
            .SubscribeAwait(async (_, ct) => await RefreshInboxAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await RefreshInboxAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        var desired = PendingInvitations.Count > 0 ? 0 : 1;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SelectedTabIndex.Value = desired;
        }
        else
        {
            await dispatcher.InvokeAsync(() => SelectedTabIndex.Value = desired);
        }
    }

    public async Task RefreshInboxAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingInvitationDto> open;
        try
        {
            open = await _inbox.GetOpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var items = open
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(p => new PendingInvitationItem
            {
                PendingSessionId = p.PendingSessionId,
                DisplayName = p.PeerName,
                Initials = ComputeInitials(p.PeerName),
                IsRelayed = p.IsRelayed,
                RelayInfoText = p.IsRelayed
                    ? $"Via relay: {p.RelayPeerName}{(string.IsNullOrWhiteSpace(p.RelayEndpoint) ? "" : $" ({p.RelayEndpoint})")}" 
                    : null
            })
            .ToList();

        void apply()
        {
            _pendingInvitations.Clear();
            foreach (var it in items)
                _pendingInvitations.Add(it);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            apply();
        }
        else
        {
            await dispatcher.InvokeAsync(apply);
        }
    }

    private async Task ExecuteAcceptAsync(object? obj)
    {
        if (obj is not PendingInvitationItem item) return;

        ApproveInvitationResult result;
        try
        {
            result = await _actions.ApproveAsync(item.PendingSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            item.StatusText = $"Failed: {ex.Message}";
            return;
        }

        switch (result)
        {
            case ApproveInvitationResult.Accepted accepted:
                item.StatusText = "Accepted";
                item.SendPath = accepted.SendPath;
                item.RequestCorrelationId = accepted.RequestCorrelationId.ToString();
                item.IsExpired = false;
                await RefreshInboxAsync().ConfigureAwait(false);
                break;
            case ApproveInvitationResult.RejectedNotReady:
                item.StatusText = "Rejected: Not Ready";
                break;
            case ApproveInvitationResult.RejectedInvalid:
                item.StatusText = "Rejected: Invalid";
                break;
            case ApproveInvitationResult.RejectedExpired:
                item.StatusText = "Rejected: Expired";
                item.IsExpired = true;
                await RefreshInboxAsync().ConfigureAwait(false);
                break;
            case ApproveInvitationResult.Failed failed:
                item.StatusText = $"Failed: {failed.ErrorMessage}";
                break;
            default:
                item.StatusText = "Failed: Unknown";
                break;
        }
    }

    private async Task ExecuteBurnAsync(object? obj)
    {
        if (obj is not PendingInvitationItem item) return;

        try
        {
            await _actions.BurnAsync(item.PendingSessionId).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await RefreshInboxAsync().ConfigureAwait(false);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(SelectedTabIndex);
        _bag.Dispose();
    }
}
