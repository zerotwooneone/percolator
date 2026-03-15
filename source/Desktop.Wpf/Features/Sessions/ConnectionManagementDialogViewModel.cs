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
using Percolator.Identity.Model;
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

public sealed class RouteModeOption
{
    public RouteModeOption(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }
    public string DisplayName { get; }
}

public sealed class RelayHostOption
{
    public RelayHostOption(Guid peerId, string displayName)
    {
        PeerId = peerId;
        DisplayName = displayName;
    }

    public Guid PeerId { get; }
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
    private readonly IPeerIdentityRepository _peerIdentities;
    private readonly IEstablishDirectSessionService _establishDirectSession;

    private readonly ObservableCollection<PendingInvitationItem> _pendingInvitations = new();
    private readonly ObservableCollection<RouteModeOption> _routeModeOptions = new();
    private readonly ObservableCollection<RelayHostOption> _relayHostOptions = new();

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public BindableReactiveProperty<string?> TargetPkhText { get; }
    public BindableReactiveProperty<string?> TargetDisplayNameText { get; }

    public ReadOnlyObservableCollection<RouteModeOption> RouteModeOptions { get; }
    public BindableReactiveProperty<RouteModeOption?> SelectedRouteMode { get; }

    public BindableReactiveProperty<string?> DirectEndpointText { get; }

    public ReadOnlyObservableCollection<RelayHostOption> RelayHostOptions { get; }
    public BindableReactiveProperty<RelayHostOption?> SelectedRelayHost { get; }

    public BindableReactiveProperty<string?> PhaseText { get; }
    public BindableReactiveProperty<string?> ErrorText { get; }

    public BindableReactiveProperty<string?> InviteTokenText { get; }

    public ReadOnlyObservableCollection<PendingInvitationItem> PendingInvitations { get; }

    public AsyncRelayCommand AcceptInvitationCommand { get; }
    public AsyncRelayCommand BurnInvitationCommand { get; }
    public AsyncRelayCommand RefreshInboxCommand { get; }

    public AsyncRelayCommand SearchAndConnectCommand { get; }

    public AsyncRelayCommand DecodeAndInitiateCommand { get; }

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
        ActiveIdentityContext active,
        IPeerIdentityRepository peerIdentities,
        IEstablishDirectSessionService establishDirectSession)
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
        _peerIdentities = peerIdentities;
        _establishDirectSession = establishDirectSession;
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        PendingInvitations = new ReadOnlyObservableCollection<PendingInvitationItem>(_pendingInvitations);

        TargetPkhText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        TargetDisplayNameText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RouteModeOptions = new ReadOnlyObservableCollection<RouteModeOption>(_routeModeOptions);
        SelectedRouteMode = new BindableReactiveProperty<RouteModeOption?>(null).AddTo(ref _bag);

        DirectEndpointText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RelayHostOptions = new ReadOnlyObservableCollection<RelayHostOption>(_relayHostOptions);
        SelectedRelayHost = new BindableReactiveProperty<RelayHostOption?>(null).AddTo(ref _bag);

        PhaseText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
        ErrorText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        InviteTokenText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);

        RefreshInboxCommand = new AsyncRelayCommand(async _ => await RefreshInboxAsync().ConfigureAwait(false));
        AcceptInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteAcceptAsync(obj).ConfigureAwait(false));
        BurnInvitationCommand = new AsyncRelayCommand(async obj => await ExecuteBurnAsync(obj).ConfigureAwait(false));

        SearchAndConnectCommand = new AsyncRelayCommand(async _ => await ExecuteNetworkSearchAsync().ConfigureAwait(false));

        DecodeAndInitiateCommand = new AsyncRelayCommand(async _ => await ExecuteImportTokenAsync().ConfigureAwait(false));

        _inboxEvents.Changed
            .SubscribeAwait(async (_, ct) => await RefreshInboxAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);

        _ = InitializeAsync().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ExecuteImportTokenAsync(CancellationToken ct = default)
    {
        ResetStatus();
        PhaseText.Value = "Decoding Token...";

        if (_active.Identity is null)
        {
            ErrorText.Value = "Identity not loaded.";
            PhaseText.Value = null;
            return;
        }

        EstablishDirectSessionRequest env;
        InviteHandshakeRequestPayload payload;
        try
        {
            var bytes = DecodeTokenToBytes(InviteTokenText.Value);
            env = EstablishDirectSessionRequest.Parser.ParseFrom(bytes);
            payload = InviteHandshakeRequestPayload.Parser.ParseFrom(env.Payload);
        }
        catch
        {
            ErrorText.Value = "Invalid token.";
            PhaseText.Value = null;
            return;
        }

        if (!env.HasInviterIdentityKey || env.InviterIdentityKey.Length == 0)
        {
            ErrorText.Value = "Token missing inviter identity key.";
            PhaseText.Value = null;
            return;
        }

        if (!env.HasPayload || env.Payload.Length == 0)
        {
            ErrorText.Value = "Token missing payload.";
            PhaseText.Value = null;
            return;
        }

        if (!env.HasPayloadSignature || env.PayloadSignature.Length == 0)
        {
            ErrorText.Value = "Token missing signature.";
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Verifying Signature...";

        if (!VerifyInvitePayloadSignature(
                inviterIdentityKeySpki: env.InviterIdentityKey.ToByteArray(),
                payloadBytes: env.Payload.ToByteArray(),
                signatureBytes: env.PayloadSignature.ToByteArray()))
        {
            ErrorText.Value = "Token signature invalid.";
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = "Queuing Invitation...";

        try
        {
            _ = await _establishDirectSession.QueueInviteAsync(
                    selfIdentityId: new Percolator.Identity.SelfId(_active.Identity.SelfIdentityId.Value),
                    inviterIdentityKeySpki: env.InviterIdentityKey.ToByteArray(),
                    payloadBytes: env.Payload.ToByteArray(),
                    payloadSignatureBytes: env.PayloadSignature.ToByteArray(),
                    isRelayed: false,
                    relayHostPeerId: null,
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
            return;
        }

        PhaseText.Value = null;

        await RefreshInboxAsync(ct).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SelectedTabIndex.Value = 0;
        }
        else
        {
            await dispatcher.InvokeAsync(() => SelectedTabIndex.Value = 0);
        }
    }

    private static bool VerifyInvitePayloadSignature(byte[] inviterIdentityKeySpki, byte[] payloadBytes, byte[] signatureBytes)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(inviterIdentityKeySpki, out _);
            return ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeTokenToBytes(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Token required.");
        }

        var t = new string(token.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Convert.FromBase64String(t);
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        await _simulatorState.InitializeAsync(ct).ConfigureAwait(false);
        await InvokeOnUiAsync(InitializeRouteModeOptions).ConfigureAwait(false);
        await RefreshRelayHostOptionsAsync(ct).ConfigureAwait(false);

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

    private void InitializeRouteModeOptions()
    {
        _routeModeOptions.Clear();
        _routeModeOptions.Add(new RouteModeOption("direct", "Direct"));
        _routeModeOptions.Add(new RouteModeOption("relay", "Via Relay Host"));
        SelectedRouteMode.Value ??= _routeModeOptions.FirstOrDefault();
    }

    private async Task RefreshRelayHostOptionsAsync(CancellationToken ct)
    {
        if (_active.Identity is null)
        {
            await InvokeOnUiAsync(() => _relayHostOptions.Clear()).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<DirectSession> sessions;
        try
        {
            sessions = await _directSessions.ListAsync(_active.Identity.SelfIdentityId.Value).ConfigureAwait(false);
        }
        catch
        {
            sessions = Array.Empty<DirectSession>();
        }

        var options = new List<RelayHostOption>();
        foreach (var s in sessions.OrderBy(x => x.RemotePeerId.Value))
        {
            var peerId = new Percolator.Identity.PeerId(s.RemotePeerId.Value);

            PeerIdentity? identity;
            try
            {
                identity = await _peerIdentities.GetByIdAsync(peerId, ct).ConfigureAwait(false);
            }
            catch
            {
                identity = null;
            }

            var name = identity?.DisplayName?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = peerId.Value.ToString()[..8];
            }

            options.Add(new RelayHostOption(peerId.Value, name));
        }

        await InvokeOnUiAsync(() =>
        {
            _relayHostOptions.Clear();
            foreach (var o in options)
                _relayHostOptions.Add(o);
            SelectedRelayHost.Value ??= _relayHostOptions.FirstOrDefault();
        }).ConfigureAwait(false);
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
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

        try
        {
            var routeMode = SelectedRouteMode.Value;
            if (routeMode is null)
            {
                ErrorText.Value = "Select a route mode.";
                PhaseText.Value = null;
                return;
            }

            if (routeMode.Key == "direct")
            {
                _ = ParseDnsEndPoint(DirectEndpointText.Value);
                PhaseText.Value = "Validated (Direct).";
                return;
            }

            if (routeMode.Key == "relay")
            {
                if (SelectedRelayHost.Value is null)
                {
                    ErrorText.Value = "Select a relay host.";
                    PhaseText.Value = null;
                    return;
                }

                PhaseText.Value = "Validated (Relay).";
                return;
            }

            ErrorText.Value = "Unknown route mode.";
            PhaseText.Value = null;
        }
        catch (Exception ex)
        {
            ErrorText.Value = ex.Message;
            PhaseText.Value = null;
        }
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

    private static DnsEndPoint ParseDnsEndPoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Endpoint required.");
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("http://".Length);
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("https://".Length);

        var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port) || port <= 0)
        {
            throw new InvalidOperationException("Invalid endpoint format. Use host:port");
        }

        return new DnsEndPoint(parts[0], port);
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
        Disposable.Dispose(TargetPkhText);
        Disposable.Dispose(TargetDisplayNameText);
        Disposable.Dispose(SelectedRouteMode);
        Disposable.Dispose(DirectEndpointText);
        Disposable.Dispose(SelectedRelayHost);
        Disposable.Dispose(PhaseText);
        Disposable.Dispose(ErrorText);
        Disposable.Dispose(InviteTokenText);
        _bag.Dispose();
    }
}
