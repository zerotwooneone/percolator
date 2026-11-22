using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class RelayRegistrationFlyoutViewModel : ViewModelBase
{
    private readonly ISimulatedPersonaHost _host;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IStatusToastService _toastService;
    private readonly IFlightRecorderStateService _flightRecorder;
    private readonly RelayProvisioningCoordinator? _relayCoordinator;

    [ObservableProperty]
    private string _relayEndpointInput = string.Empty;

    [ObservableProperty]
    private bool _seedPreKeysToRelay = true;

    [ObservableProperty]
    private bool _isRegistering;

    [ObservableProperty]
    private string? _errorMessage;

    public RelayRegistrationFlyoutViewModel(
        ISimulatedPersonaHost host,
        ISimulatedWorldStateService worldState,
        RelayProvisioningCoordinator? relayCoordinator,
        IStatusToastService toastService,
        IFlightRecorderStateService flightRecorder)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _relayCoordinator = relayCoordinator;
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
    }

    [RelayCommand]
    public async Task RegisterRelayAsync()
    {
        ErrorMessage = null;
        IsRegistering = true;

        try
        {
            var input = RelayEndpointInput?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                ErrorMessage = "Please enter a relay endpoint (host:port).";
                return;
            }

            if (input.StartsWith("relay://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Invalid relay endpoint format. Expected percolator://host:port or host:port (e.g. 127.0.0.1:5100).";
                return;
            }

            if (input.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                input = input["percolator://".Length..];
            }

            input = input.TrimEnd('/');
            var colonIndex = input.LastIndexOf(':');
            if (colonIndex <= 0 || !int.TryParse(input[(colonIndex + 1)..], out var port) || port < 1 || port > 65535)
            {
                ErrorMessage = "Invalid relay endpoint format. Expected percolator://host:port or host:port (e.g. 127.0.0.1:5100).";
                return;
            }

            var host = input[..colonIndex].Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                ErrorMessage = "Invalid host specified for relay endpoint.";
                return;
            }

            using var scope = _host.Services.CreateScope();
            var relayDirectory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
            var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();

            var relayEndpoint = RelayEndpoint.FromHostAndPort(host, port);

            var existingRelays = await relayDirectory.ListAsync().ConfigureAwait(true);
            var existing = existingRelays.FirstOrDefault(r => r.Endpoint.Equals(relayEndpoint));
            PublicIdentityId relayIdentityId;

            if (existing != null)
            {
                relayIdentityId = existing.RelayIdentityId;
            }
            else
            {
                var localRelay = _worldState.GetAllRelayHosts().FirstOrDefault(r =>
                    r.BoundPort == port &&
                    (string.Equals(r.BoundEndpoint.Host, host, StringComparison.OrdinalIgnoreCase) ||
                     host == "127.0.0.1" || host == "localhost"));

                relayIdentityId = localRelay?.RelayIdentityId ?? PublicIdentityId.New();
                var regResult = await relayDirectory.RegisterRemoteRelayAsync(
                    relayIdentityId,
                    relayEndpoint,
                    pinnedCertificate: null).ConfigureAwait(true);

                if (regResult.IsFailure)
                {
                    ErrorMessage = regResult.Error.Description;
                    return;
                }
            }

            var linkResult = await relayDirectory.LinkIdentityAsync(
                relayIdentityId,
                _host.PublicIdentityId,
                _host.MailboxRoutingToken,
                _host.MailboxDrainToken).ConfigureAwait(true);

            if (linkResult.IsFailure)
            {
                ErrorMessage = linkResult.Error.Description;
                return;
            }

            var provisionResult = await relayDirectory.EnsureMailboxProvisionedAsync(
                relayIdentityId,
                _host.PublicIdentityId).ConfigureAwait(true);

            if (provisionResult.IsFailure)
            {
                ErrorMessage = provisionResult.Error.Description;
                return;
            }

            if (SeedPreKeysToRelay)
            {
                var tokensResult = await relayDirectory.GetMailboxTokensAsync(
                    _host.PublicIdentityId,
                    relayIdentityId).ConfigureAwait(true);

                if (tokensResult.IsSuccess)
                {
                    await provisioner.PublishPreKeyBundleAsync(
                        _host.PublicIdentityId,
                        DeviceId.Primary,
                        relayEndpoint,
                        tokensResult.Value!,
                        25u).ConfigureAwait(true);
                }
            }

            var matchedRelayHost = _worldState.GetAllRelayHosts().FirstOrDefault(r =>
                r.RelayIdentityId == relayIdentityId ||
                (r.BoundPort == port &&
                 (string.Equals(r.BoundEndpoint.Host, host, StringComparison.OrdinalIgnoreCase) ||
                  host == "127.0.0.1" || host == "localhost")));

            var targetRelay = matchedRelayHost ?? new RemoteRelayHost(relayIdentityId, relayEndpoint);
            await _host.AssignHomeRelayAsync(targetRelay).ConfigureAwait(true);

            _flightRecorder.RecordStep(new RegisterMailboxesStep(targetRelay.DisplayName, new[] { _host.DisplayName }));

            _toastService.ShowToast(
                "Relay Registered",
                $"Mailbox registered on '{relayEndpoint}'.",
                ToastSeverity.Success);

            RelayEndpointInput = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to register relay: {ex.Message}";
        }
        finally
        {
            IsRegistering = false;
        }
    }
}
