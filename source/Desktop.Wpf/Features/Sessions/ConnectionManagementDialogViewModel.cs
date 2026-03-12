using Desktop.Wpf.Shared.Mvvm;
using R3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using Desktop.Wpf.Features.Simulator;
using Percolator.Application.Services;
using Percolator.Cryptography;

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

public sealed class TransportRouteOption
{
    public TransportRouteOption(Guid? relayHostPeerId, string displayName)
    {
        RelayHostPeerId = relayHostPeerId;
        DisplayName = displayName;
    }

    // null => Direct P2P (local mesh)
    public Guid? RelayHostPeerId { get; }
    public string DisplayName { get; }
}

public sealed class ConnectionManagementDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;

    private readonly IMainInvitationInbox _inbox;
    private readonly IMainInvitationActions _actions;
    private readonly IMainInvitationInboxEvents _inboxEvents;

    private readonly IMainReverseSignalInviteFactory _reverseSignalInvites;
    private readonly IGrpcSessionService _grpcSessions;
    private readonly IMessageTransportService _transport;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IDirectSessionRepository _directSessions;
    private readonly ISimulatorStateService _simulatorState;
    private readonly ActiveIdentityContext _active;

    private readonly ObservableCollection<PendingInvitationItem> _pendingInvitations = new();
    private readonly ObservableCollection<TransportRouteOption> _routeOptions = new();

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public ReadOnlyObservableCollection<TransportRouteOption> RouteOptions { get; }
    public BindableReactiveProperty<TransportRouteOption?> SelectedRoute { get; }
    public BindableReactiveProperty<string?> TargetPkhText { get; }

    public BindableReactiveProperty<string?> PhaseText { get; }
    public BindableReactiveProperty<string?> ErrorText { get; }

    public ReadOnlyObservableCollection<PendingInvitationItem> PendingInvitations { get; }

    public AsyncRelayCommand AcceptInvitationCommand { get; }
    public AsyncRelayCommand BurnInvitationCommand { get; }
    public AsyncRelayCommand RefreshInboxCommand { get; }

    public AsyncRelayCommand SearchAndConnectCommand { get; }

    public ConnectionManagementDialogViewModel(
        IMainInvitationInbox inbox,
        IMainInvitationActions actions,
        IMainInvitationInboxEvents inboxEvents,
        IMainReverseSignalInviteFactory reverseSignalInvites,
        IGrpcSessionService grpcSessions,
        IMessageTransportService transport,
        ISecureMessagingService secureMessaging,
        IDirectSessionRepository directSessions,
        ISimulatorStateService simulatorState,
        ActiveIdentityContext active)
    {
        _inbox = inbox;
        _actions = actions;
        _inboxEvents = inboxEvents;
        _reverseSignalInvites = reverseSignalInvites;
        _grpcSessions = grpcSessions;
        _transport = transport;
        _secureMessaging = secureMessaging;
        _directSessions = directSessions;
        _simulatorState = simulatorState;
        _active = active;
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        PendingInvitations = new ReadOnlyObservableCollection<PendingInvitationItem>(_pendingInvitations);

        RouteOptions = new ReadOnlyObservableCollection<TransportRouteOption>(_routeOptions);
        SelectedRoute = new BindableReactiveProperty<TransportRouteOption?>(null).AddTo(ref _bag);
        TargetPkhText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        PhaseText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        ErrorText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RefreshInboxCommand = new AsyncRelayCommand(async _ => await RefreshInboxAsync().ConfigureAwait(false));
        AcceptInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteAcceptAsync(obj).ConfigureAwait(false));
        BurnInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteBurnAsync(obj).ConfigureAwait(false));

        SearchAndConnectCommand = new AsyncRelayCommand(async _ => await ExecuteNetworkSearchAsync().ConfigureAwait(false));

        _inboxEvents.Changed
            .SubscribeAwait(async (_, ct) => await RefreshInboxAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await _simulatorState.InitializeAsync(ct).ConfigureAwait(false);
        RefreshRouteOptions();

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

    private void RefreshRouteOptions()
    {
        _routeOptions.Clear();
        _routeOptions.Add(new TransportRouteOption(relayHostPeerId: null, displayName: "Direct P2P (Local Mesh)"));

        foreach (var p in _simulatorState.Peers
                     .Where(x => x.IsOnline && x.Relay?.IsRelayCapable == true)
                     .OrderBy(x => x.DisplayName ?? x.PeerId.ToString()))
        {
            var name = string.IsNullOrWhiteSpace(p.DisplayName) ? p.PeerId.ToString()[..8] : p.DisplayName!;
            _routeOptions.Add(new TransportRouteOption(p.PeerId, name));
        }

        SelectedRoute.Value ??= _routeOptions.FirstOrDefault();
    }

    private async Task ExecuteNetworkSearchAsync()
    {
        ResetStatus();

        PhaseText.Value = "Validating...";

        byte[] targetPkh;
        try
        {
            targetPkh = ParsePkh(TargetPkhText.Value);
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
            return;
        }

        var targetPeer = TryResolveSimulatedPeerByPkh(targetPkh);
        if (targetPeer is null)
        {
            ErrorText.Value = "Target not found in simulator.";
            PhaseText.Value = null;
            return;
        }

        EstablishDirectSessionRequest invite;
        try
        {
            invite = _reverseSignalInvites.CreateInvite();
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
            return;
        }

        var route = SelectedRoute.Value;
        if (route is null)
        {
            ErrorText.Value = "Select a transport route.";
            PhaseText.Value = null;
            return;
        }

        try
        {
            if (route.RelayHostPeerId is null)
            {
                PhaseText.Value = "Sending Invite (Direct)...";
                var endpoint = new DnsEndPoint(targetPeer.Connection.Host, targetPeer.Connection.Port);
                _ = await _grpcSessions.EstablishDirectSessionAsync(endpoint, invite).ConfigureAwait(false);
                PhaseText.Value = null;
                return;
            }

            PhaseText.Value = "Sending Invite (Via Relay)...";
            await SendInviteViaRelayAsync(route.RelayHostPeerId.Value, targetPkh, invite.ToByteArray(), CancellationToken.None)
                .ConfigureAwait(false);
            PhaseText.Value = null;
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
        }
    }

    private SimulatedPeerDto? TryResolveSimulatedPeerByPkh(byte[] targetPkh)
    {
        foreach (var p in _simulatorState.Peers)
        {
            var spki = p.ReverseSignalKeys?.IdentitySigningKeySpki;
            if (spki is null || spki.Length == 0) continue;

            var pkh = SHA256.HashData(spki);
            if (pkh.SequenceEqual(targetPkh))
            {
                return p;
            }
        }

        return null;
    }

    private async Task SendInviteViaRelayAsync(
        Guid relayHostPeerId,
        byte[] recipientPkh,
        byte[] inviteBytes,
        CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            throw new InvalidOperationException("Identity not loaded.");
        }

        var relaySession = await _directSessions.GetByRemotePeerIdAsync(
                new Percolator.Network.PeerId(relayHostPeerId),
                _active.Identity.SelfIdentityId.Value)
            .ConfigureAwait(false);
        if (relaySession is null)
        {
            throw new InvalidOperationException("No session to relay host.");
        }

        var mqReq = new EnqueueOpaqueMessageRequest
        {
            Version = 1,
            RecipientPublicKeyHash = ByteString.CopyFrom(recipientPkh),
            MessageBlob = ByteString.CopyFrom(inviteBytes)
        };
        var toRelay = new InternalEnvelope
        {
            MessageQueueEnvelope = new MessageQueueEnvelope
            {
                Version = 1,
                EnqueueOpaqueMessageRequest = mqReq
            }
        };

        var relayPlain = new Plaintext(toRelay.ToByteArray());
        var relaySessionId = new SessionId(relaySession.SessionId.Value);
        var relayDirectSessionId = new DirectSessionId(relaySession.SessionId.Value);
        var relayCipher = await _secureMessaging.EncryptAsync(relaySessionId, relayPlain, ct).ConfigureAwait(false);

        // EstablishDirectSessionRequest is one-way; no response required.
        _ = await _transport.SendMessageAsync(
                new Percolator.Identity.PeerId(relayHostPeerId),
                relayDirectSessionId,
                relayCipher,
                ct)
            .ConfigureAwait(false);
    }

    private static byte[] ParsePkh(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Target PKH required.");

        var t = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(2);

        if (TryParseHex(t, out var hexBytes))
            return hexBytes;

        try
        {
            return Convert.FromBase64String(t);
        }
        catch
        {
            throw new InvalidOperationException("PKH must be hex or base64.");
        }
    }

    private static bool TryParseHex(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length % 2 != 0) return false;

        try
        {
            bytes = Convert.FromHexString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ResetStatus()
    {
        PhaseText.Value = null;
        ErrorText.Value = null;
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
        Disposable.Dispose(SelectedRoute);
        Disposable.Dispose(TargetPkhText);
        Disposable.Dispose(PhaseText);
        Disposable.Dispose(ErrorText);
        _bag.Dispose();
    }
}
