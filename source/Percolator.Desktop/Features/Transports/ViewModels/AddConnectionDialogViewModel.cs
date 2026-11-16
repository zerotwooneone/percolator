// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Desktop.Features.Transports.ViewModels;

public sealed partial class AddConnectionDialogViewModel : ViewModelBase
{
    private readonly IPeerInvitationCodec _invitationCodec;
    private readonly IPeerConnectionService _peerConnectionService;
    private readonly IRelayDirectory _relayDirectory;
    private readonly IRelayMailboxProvisioner _relayProvisioner;
    private readonly IClipboardService _clipboardService;
    private readonly IPersonaDirectory _personaDirectory;
    private readonly ILogger<AddConnectionDialogViewModel> _logger;
    private readonly TimeProvider _timeProvider;

    private PublicIdentityId? _activePersonaId;

    public AddConnectionDialogViewModel(
        IPeerInvitationCodec invitationCodec,
        IPeerConnectionService peerConnectionService,
        IRelayDirectory relayDirectory,
        IRelayMailboxProvisioner relayProvisioner,
        IClipboardService clipboardService,
        IPersonaDirectory personaDirectory,
        ILogger<AddConnectionDialogViewModel> logger,
        TimeProvider timeProvider)
    {
        _invitationCodec = invitationCodec ?? throw new ArgumentNullException(nameof(invitationCodec));
        _peerConnectionService = peerConnectionService ?? throw new ArgumentNullException(nameof(peerConnectionService));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _relayProvisioner = relayProvisioner ?? throw new ArgumentNullException(nameof(relayProvisioner));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _personaDirectory = personaDirectory ?? throw new ArgumentNullException(nameof(personaDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler? RequestClose;

    public AddConnectionDialogResult? Result { get; private set; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _peerInvitationInput = string.Empty;

    [ObservableProperty]
    private string _relayEndpointInput = string.Empty;

    [ObservableProperty]
    private bool _seedPreKeysToRelay;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _probeStatusText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public void Initialize(PublicIdentityId activePersonaId)
    {
        _activePersonaId = activePersonaId;
    }

    partial void OnPeerInvitationInputChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed["percolator://".Length..].Trim();
                if (stripped != value)
                {
                    PeerInvitationInput = stripped;
                }
            }
        }
    }

    partial void OnRelayEndpointInputChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed["percolator://".Length..].Trim();
                if (stripped != value)
                {
                    RelayEndpointInput = stripped;
                }
            }
            else if (trimmed.StartsWith("relay://", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed["relay://".Length..].Trim();
                if (stripped != value)
                {
                    RelayEndpointInput = stripped;
                }
            }
        }
    }

    [RelayCommand]
    private async Task PasteClipboardAsync()
    {
        var text = await _clipboardService.GetTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(text))
        {
            text = text.Trim();
            if (text.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                text = text["percolator://".Length..].Trim();
            }

            PeerInvitationInput = text;
        }
    }

    [RelayCommand]
    private async Task SubmitPeerConnectionAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(PeerInvitationInput))
        {
            ErrorMessage = "Please enter a peer invitation.";
            return;
        }

        var decodeResult = _invitationCodec.Decode(PeerInvitationInput);
        if (decodeResult.IsFailure)
        {
            ErrorMessage = decodeResult.Error.Description;
            return;
        }

        var payload = decodeResult.Value!;
        IsProbing = true;
        ProbeStatusText = "Resolving endpoint...";

        Uri directUri;
        try
        {
            var rawHost = payload.EndpointHosts.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "127.0.0.1";
            var port = payload.PortRange?.MinPort ?? 5100;

            if (rawHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                rawHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = new Uri(rawHost);
                directUri = new UriBuilder(parsed) { Scheme = "https", Port = port }.Uri;
            }
            else
            {
                directUri = new UriBuilder("https", rawHost, port).Uri;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse endpoint URI for peer invitation {PeerId}", payload.IdentityId);
            ErrorMessage = $"Invalid endpoint address in invitation: {ex.Message}";
            IsProbing = false;
            ProbeStatusText = string.Empty;
            return;
        }

        ProbeStatusText = "Probing network route...";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3), _timeProvider);
        try
        {
            var connResult = await _peerConnectionService.ConnectToDirectPeerAsync(
                payload.IdentityId,
                payload.RoutingToken,
                payload.DeliveryToken,
                directUri,
                ct: cts.Token).ConfigureAwait(true);

            if (connResult.IsSuccess)
            {
                _logger.LogInformation("Direct connection to peer {PeerId} succeeded at {Endpoint}", payload.IdentityId, directUri);
                Result = new AddConnectionDialogResult(
                    OutboundConnectionStatus.Connected,
                    Invitation: payload,
                    PeerId: payload.IdentityId);
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _logger.LogInformation("Direct peer {PeerId} probe returned failure ({Error}). Staging as PendingOffline.",
                    payload.IdentityId, connResult.Error.Description);
                Result = new AddConnectionDialogResult(
                    OutboundConnectionStatus.PendingOffline,
                    Invitation: payload,
                    PeerId: payload.IdentityId);
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Direct peer {PeerId} probe timed out after 3 seconds. Staging as PendingOffline.", payload.IdentityId);
            Result = new AddConnectionDialogResult(
                OutboundConnectionStatus.PendingOffline,
                Invitation: payload,
                PeerId: payload.IdentityId);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception encountered during direct peer probe for {PeerId}. Staging as PendingOffline.", payload.IdentityId);
            Result = new AddConnectionDialogResult(
                OutboundConnectionStatus.PendingOffline,
                Invitation: payload,
                PeerId: payload.IdentityId);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsProbing = false;
            ProbeStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SubmitRelayConnectionAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(RelayEndpointInput))
        {
            ErrorMessage = "Please enter a relay endpoint (host:port).";
            return;
        }

        var clean = RelayEndpointInput.Trim();
        if (clean.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["percolator://".Length..];
        }
        else if (clean.StartsWith("relay://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["relay://".Length..];
        }
        else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["https://".Length..];
        }
        else if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["http://".Length..];
        }

        clean = clean.TrimEnd('/');
        var colonIndex = clean.LastIndexOf(':');
        if (colonIndex <= 0 || !int.TryParse(clean[(colonIndex + 1)..], out var port) || port < 1 || port > 65535)
        {
            ErrorMessage = "Invalid relay endpoint format. Expected host:port (e.g. 127.0.0.1:5100).";
            return;
        }

        var host = clean[..colonIndex].Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            ErrorMessage = "Invalid host specified for relay endpoint.";
            return;
        }

        PublicIdentityId activePersonaId;
        if (_activePersonaId.HasValue && _activePersonaId.Value.IsValid)
        {
            activePersonaId = _activePersonaId.Value;
        }
        else
        {
            var personas = await _personaDirectory.ListAsync().ConfigureAwait(true);
            if (personas.Count == 0)
            {
                ErrorMessage = "No active persona found. Please select a persona first.";
                return;
            }

            activePersonaId = personas[0].IdentityId;
        }

        IsProbing = true;
        ProbeStatusText = "Connecting to relay...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
            var relayEndpoint = RelayEndpoint.FromHostAndPort(host, port);

            // Deduplicate: If relay endpoint is already known, reuse its identity registration.
            var existingRelays = await _relayDirectory.ListAsync(cts.Token).ConfigureAwait(true);
            var existing = existingRelays.FirstOrDefault(r => r.Endpoint.Equals(relayEndpoint));
            PublicIdentityId relayIdentityId;

            if (existing != null)
            {
                relayIdentityId = existing.RelayIdentityId;
                _logger.LogInformation("Reusing existing relay registration {RelayIdentityId} for endpoint {Endpoint}",
                    relayIdentityId, relayEndpoint);
            }
            else
            {
                relayIdentityId = PublicIdentityId.New();
                var regResult = await _relayDirectory.RegisterRemoteRelayAsync(
                    relayIdentityId,
                    relayEndpoint,
                    pinnedCertificate: null,
                    ct: cts.Token).ConfigureAwait(true);

                if (regResult.IsFailure)
                {
                    _logger.LogWarning("Failed to register remote relay {Endpoint}: {Error}", relayEndpoint, regResult.Error.Description);
                    ErrorMessage = regResult.Error.Description;
                    return;
                }
            }

            var linkResult = await _relayDirectory.LinkIdentityAsync(
                relayIdentityId,
                activePersonaId,
                ct: cts.Token).ConfigureAwait(true);

            if (linkResult.IsFailure)
            {
                _logger.LogWarning("Failed to link persona {PersonaId} to relay {RelayId}: {Error}",
                    activePersonaId, relayIdentityId, linkResult.Error.Description);
                ErrorMessage = linkResult.Error.Description;
                return;
            }

            var provisionResult = await _relayDirectory.EnsureMailboxProvisionedAsync(
                relayIdentityId,
                activePersonaId,
                ct: cts.Token).ConfigureAwait(true);

            if (provisionResult.IsFailure)
            {
                _logger.LogWarning("Failed to provision mailbox on relay {RelayId}: {Error}",
                    relayIdentityId, provisionResult.Error.Description);
                ErrorMessage = provisionResult.Error.Description;
                return;
            }

            if (SeedPreKeysToRelay)
            {
                ProbeStatusText = "Publishing prekeys to relay...";
                var tokensResult = await _relayDirectory.GetMailboxTokensAsync(
                    activePersonaId,
                    relayIdentityId,
                    cts.Token).ConfigureAwait(true);

                if (tokensResult.IsSuccess)
                {
                    var pubResult = await _relayProvisioner.PublishPreKeyBundleAsync(
                        activePersonaId,
                        DeviceId.Primary,
                        relayEndpoint,
                        tokensResult.Value!,
                        newOneTimePreKeyCount: 20,
                        ct: cts.Token).ConfigureAwait(true);

                    if (pubResult.IsFailure)
                    {
                        _logger.LogWarning("Failed to publish prekeys to relay {RelayId}: {Error}",
                            relayIdentityId, pubResult.Error.Description);
                    }
                }
            }

            Result = new AddConnectionDialogResult(
                OutboundConnectionStatus.RelayAdded,
                RelayEndpoint: relayEndpoint.ToString());
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect and add relay {Host}:{Port}", host, port);
            ErrorMessage = $"Failed to add relay: {ex.Message}";
        }
        finally
        {
            IsProbing = false;
            ProbeStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = new AddConnectionDialogResult(OutboundConnectionStatus.Cancelled);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
