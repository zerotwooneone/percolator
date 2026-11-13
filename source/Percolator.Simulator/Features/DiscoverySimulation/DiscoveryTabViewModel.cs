using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.DiscoverySimulation;

internal sealed partial class DiscoveryTabViewModel : ViewModelBase
{
    private readonly ISimulatedPersonaHost _host;
    private readonly DiscoverySimulationCoordinator _discoveryCoordinator;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IStatusToastService _toastService;
    private readonly IVirtualClockStateService _virtualClock;

    public ObservableCollection<DiscoveredPeerItemViewModel> NearbyCandidates { get; } = new();

    [ObservableProperty]
    private string _sharedSecretHex = "deadbeefcafebabe0123456789abcdefdeadbeefcafebabe0123456789abcdef";

    [ObservableProperty]
    private string _targetPeerIdInput = string.Empty;

    [ObservableProperty]
    private string _resolutionResultText = string.Empty;

    [ObservableProperty]
    private bool _isPublishing;

    [ObservableProperty]
    private bool _isResolving;

    [ObservableProperty]
    private bool _isQueryingNearby;

    public string CurrentEpochText => $"Epoch: {_virtualClock.CurrentDiscoveryEpoch}";

    public string DerivedLocatorHex
    {
        get
        {
            try
            {
                var secret = ParseSharedSecret(SharedSecretHex);
                var epoch = new DiscoveryEpoch(_virtualClock.CurrentDiscoveryEpoch);
                var loc = BlindedLocator.Derive(_host.PublicIdentityId, secret, epoch);
                return Convert.ToHexString(loc.Span);
            }
            catch
            {
                return "Invalid secret";
            }
        }
    }

    public DiscoveryTabViewModel(
        ISimulatedPersonaHost host,
        DiscoverySimulationCoordinator discoveryCoordinator,
        ISimulatedWorldStateService worldState,
        IStatusToastService toastService,
        IVirtualClockStateService virtualClock)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _discoveryCoordinator = discoveryCoordinator ?? throw new ArgumentNullException(nameof(discoveryCoordinator));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));

        _virtualClock.CurrentUtcNow
            .Subscribe(_ =>
            {
                OnPropertyChanged(nameof(CurrentEpochText));
                OnPropertyChanged(nameof(DerivedLocatorHex));
            })
            .AddTo(ref Disposables);
    }

    partial void OnSharedSecretHexChanged(string value)
    {
        OnPropertyChanged(nameof(DerivedLocatorHex));
    }

    [RelayCommand]
    private async Task PublishBlindedLocatorAsync()
    {
        if (IsPublishing) return;

        var relay = _host.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast("Relay Missing", "Spawn a Relay node first in the top toolbar.", ToastSeverity.Warning);
            return;
        }

        byte[] secret;
        try
        {
            secret = ParseSharedSecret(SharedSecretHex);
        }
        catch
        {
            _toastService.ShowToast("Invalid Secret", "Shared secret must be a valid hex string.", ToastSeverity.Error);
            return;
        }

        IsPublishing = true;
        try
        {
            var loc = await UiTaskRunner.RunAsync(
                () => _discoveryCoordinator.PublishBlindedLocatorAsync(_host, relay, secret),
                category: "Rendezvous",
                toastService: _toastService);

            if (loc.IsValid)
            {
                _toastService.ShowToast("Locator Published", $"Published Blinded Locator to '{relay.DisplayName}'.", ToastSeverity.Success);
            }
        }
        finally
        {
            IsPublishing = false;
        }
    }

    [RelayCommand]
    private async Task ResolvePeerLocatorAsync()
    {
        if (IsResolving) return;

        var relay = _host.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast("Relay Missing", "Spawn a Relay node first in the top toolbar.", ToastSeverity.Warning);
            return;
        }

        byte[] secret;
        try
        {
            secret = ParseSharedSecret(SharedSecretHex);
        }
        catch
        {
            _toastService.ShowToast("Invalid Secret", "Shared secret must be a valid hex string.", ToastSeverity.Error);
            return;
        }

        PublicIdentityId targetPeerId;
        if (string.IsNullOrWhiteSpace(TargetPeerIdInput))
        {
            _toastService.ShowToast("Target Missing", "Specify Target Peer ID to resolve.", ToastSeverity.Warning);
            return;
        }
        try
            {
            var bytes = Convert.FromHexString(TargetPeerIdInput.Trim());
            targetPeerId = PublicIdentityId.FromBytes(bytes);
        }
        catch
        {
            _toastService.ShowToast("Invalid Peer ID", "Target Peer ID must be valid hex bytes.", ToastSeverity.Error);
            return;
        }

        IsResolving = true;
        try
        {
            var matches = await UiTaskRunner.RunAsync(
                () => _discoveryCoordinator.ResolveBlindedLocatorAsync(_host, relay, targetPeerId, secret),
                category: "Rendezvous",
                toastService: _toastService);

            if (matches != null && matches.Count > 0)
            {
                var match = matches[0];
                ResolutionResultText = $"✅ Match found! Route: {match.RouteDescriptor}";
                _toastService.ShowToast("Locator Resolved", $"Found match for peer: {match.RouteDescriptor}", ToastSeverity.Success);
            }
            else
            {
                ResolutionResultText = "❌ No matching locator found in 3-day window.";
                _toastService.ShowToast("Not Found", "No active rendezvous locator found on relay.", ToastSeverity.Informational);
            }
        }
        finally
        {
            IsResolving = false;
        }
    }

    [RelayCommand]
    private async Task QueryNearbyPeersAsync()
    {
        if (IsQueryingNearby) return;

        var relay = _host.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast("Relay Missing", "Spawn a Relay node first in the top toolbar.", ToastSeverity.Warning);
            return;
        }

        IsQueryingNearby = true;
        try
        {
            var descriptors = await UiTaskRunner.RunAsync(
                () => _discoveryCoordinator.QueryNearbyPeersAsync(_host, relay, limit: 10),
                category: "Discovery",
                toastService: _toastService);

            NearbyCandidates.Clear();
            if (descriptors != null)
            {
                using var scope = _host.Services.CreateScope();
                var contactRepo = scope.ServiceProvider.GetRequiredService<IPeerContactRepository>();
                var profileRepo = scope.ServiceProvider.GetRequiredService<IPeerProfileRepository>();

                foreach (var d in descriptors)
                {
                    var peerId = PublicIdentityId.FromBytes(d.PeerId.Span);
                    string name;
                    var contact = await contactRepo.GetByPeerIdAsync(_host.PublicIdentityId, peerId).ConfigureAwait(false);
                    if (contact != null && !string.IsNullOrWhiteSpace(contact.Nickname.Value) && contact.Nickname.Value != peerId.ToString())
                    {
                        name = contact.Nickname.Value;
                    }
                    else
                    {
                        var profile = await profileRepo.GetByPeerIdAsync(peerId).ConfigureAwait(false);
                        name = profile?.DisplayName ?? $"Peer-{Convert.ToHexString(d.PeerId.Span)[..6]}";
                    }

                    NearbyCandidates.Add(new DiscoveredPeerItemViewModel(
                        peerId,
                        name,
                        d.RelayEndpoint,
                        (id, ep) => ConnectDiscoveredPeerAsync(id, ep)));
                }

                _toastService.ShowToast("Nearby Discovery", $"Found {descriptors.Count} discoverable peer(s).", ToastSeverity.Success);
            }
        }
        finally
        {
            IsQueryingNearby = false;
        }
    }

    private async Task<bool> ConnectDiscoveredPeerAsync(PublicIdentityId peerId, string relayEndpoint)
    {
        return await UiTaskRunner.RunAsync(
            () => _discoveryCoordinator.ConnectDiscoveredPeerAsync(_host, peerId, relayEndpoint),
            category: "Handshake",
            toastService: _toastService);
    }

    private static byte[] ParseSharedSecret(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length >= 32 && trimmed.All(Uri.IsHexDigit))
        {
            try
            {
                return Convert.FromHexString(trimmed);
            }
            catch { }
        }
        var utf8 = System.Text.Encoding.UTF8.GetBytes(trimmed);
        if (utf8.Length >= 16)
        {
            return utf8;
        }
        return SHA256.HashData(utf8);
    }
}
