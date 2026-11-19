// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.Transports.ViewModels;


/// <summary>
/// ViewModel managing the "Share My Invitation" dialog.
/// Encodes local identity metadata, cryptographic routing tokens, and customized port ranges
/// into <c>percolator://invite?...</c> URIs and QR code image bytes.
/// </summary>
public sealed partial class ShareInvitationDialogViewModel : ViewModelBase
{
    private readonly IDialogContext<ShareInvitationDialogParams> _dialogContext;
    private readonly IPeerInvitationCodec _invitationCodec;
    private readonly IClipboardService _clipboardService;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly ILogger<ShareInvitationDialogViewModel> _logger;

    private readonly PublicIdentityId _identityId;
    private readonly BlindedRoutingToken _routingToken;
    private readonly DeliveryToken _deliveryToken;
    private readonly IReadOnlyList<string> _endpointHosts;
    private readonly int _boundPort;

    public ShareInvitationDialogViewModel(
        IDialogContext<ShareInvitationDialogParams> dialogContext,
        IPeerInvitationCodec invitationCodec,
        IClipboardService clipboardService,
        IQrCodeGenerator qrCodeGenerator,
        ILogger<ShareInvitationDialogViewModel> logger)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _invitationCodec = invitationCodec ?? throw new ArgumentNullException(nameof(invitationCodec));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _qrCodeGenerator = qrCodeGenerator ?? throw new ArgumentNullException(nameof(qrCodeGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var p = dialogContext.Data ?? throw new ArgumentNullException(nameof(dialogContext.Data));
        _identityId = p.IdentityId;
        _routingToken = p.RoutingToken;
        _deliveryToken = p.DeliveryToken;
        _endpointHosts = p.EndpointHosts ?? Array.Empty<string>();
        _boundPort = p.BoundPort;

        UpdateInvitationPayload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRangeVisible))]
    private PortRangePrivacyPreset _selectedPrivacyPreset = PortRangePrivacyPreset.DefaultRange;

    [ObservableProperty]
    private int _customRangeMin = 5100;

    [ObservableProperty]
    private int _customRangeMax = 5120;

    [ObservableProperty]
    private string _invitationUrl = string.Empty;

    [ObservableProperty]
    private byte[]? _qrCodePngBytes;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True when custom port range input controls should be displayed.</summary>
    public bool IsCustomRangeVisible => SelectedPrivacyPreset == PortRangePrivacyPreset.CustomRange;

    /// <summary>Two-way bound boolean property for DefaultRange radio selection ([VM-015]).</summary>
    public bool IsDefaultRangePreset
    {
        get => SelectedPrivacyPreset == PortRangePrivacyPreset.DefaultRange;
        set { if (value) SelectedPrivacyPreset = PortRangePrivacyPreset.DefaultRange; }
    }

    /// <summary>Two-way bound boolean property for CustomRange radio selection ([VM-015]).</summary>
    public bool IsCustomRangePreset
    {
        get => SelectedPrivacyPreset == PortRangePrivacyPreset.CustomRange;
        set { if (value) SelectedPrivacyPreset = PortRangePrivacyPreset.CustomRange; }
    }

    /// <summary>Two-way bound boolean property for SinglePort radio selection ([VM-015]).</summary>
    public bool IsSinglePortPreset
    {
        get => SelectedPrivacyPreset == PortRangePrivacyPreset.SinglePort;
        set { if (value) SelectedPrivacyPreset = PortRangePrivacyPreset.SinglePort; }
    }

    /// <summary>Currently bound listening port for the active persona.</summary>
    public int BoundPort => _boundPort;

    partial void OnSelectedPrivacyPresetChanged(PortRangePrivacyPreset value)
    {
        OnPropertyChanged(nameof(IsDefaultRangePreset));
        OnPropertyChanged(nameof(IsCustomRangePreset));
        OnPropertyChanged(nameof(IsSinglePortPreset));
        OnPropertyChanged(nameof(IsCustomRangeVisible));
        UpdateInvitationPayload();
    }
    partial void OnCustomRangeMinChanged(int value) => UpdateInvitationPayload();
    partial void OnCustomRangeMaxChanged(int value) => UpdateInvitationPayload();

    /// <summary>
    /// Re-encodes the invitation URL and QR code based on the current privacy preset and range settings.
    /// </summary>
    [RelayCommand]
    public void UpdateInvitationPayload()
    {
        try
        {
            PortRange? portRange = SelectedPrivacyPreset switch
            {
                PortRangePrivacyPreset.DefaultRange => null,
                PortRangePrivacyPreset.CustomRange => new PortRange(
                    Math.Clamp(Math.Min(CustomRangeMin, CustomRangeMax), 1, 65535),
                    Math.Clamp(Math.Max(CustomRangeMin, CustomRangeMax), 1, 65535)),
                PortRangePrivacyPreset.SinglePort => PortRange.Single(Math.Clamp(_boundPort, 1, 65535)),
                _ => null
            };

            var payload = new PeerInvitationPayload(
                _identityId,
                _routingToken,
                _deliveryToken,
                _endpointHosts,
                portRange);

            InvitationUrl = _invitationCodec.Encode(payload);
            QrCodePngBytes = _qrCodeGenerator.GeneratePngBytes(InvitationUrl);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encode peer invitation payload");
            ErrorMessage = "Failed to generate invitation.";
        }
    }

    /// <summary>
    /// Copies the generated invitation URL to the system clipboard.
    /// </summary>
    [RelayCommand]
    public async Task CopyInvitationUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(InvitationUrl)) return;

        try
        {
            await _clipboardService.SetTextAsync(InvitationUrl).ConfigureAwait(true);
            StatusMessage = "Invitation link copied to clipboard!";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy invitation URL to clipboard");
            ErrorMessage = "Could not copy to clipboard.";
        }
    }

    /// <summary>
    /// Requests dismissal of the dialog window.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        _dialogContext.Close();
    }

    protected override void DisposeCore()
    {
        _deliveryToken.Dispose();
        base.DisposeCore();
    }
}
