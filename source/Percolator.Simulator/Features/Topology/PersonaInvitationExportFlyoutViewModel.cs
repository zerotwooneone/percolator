using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Application.Invitations;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class PersonaInvitationExportFlyoutViewModel : ViewModelBase
{
    private readonly ISimulatedPersonaHost _host;
    private readonly IPeerInvitationCodec _codec;
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomHostVisible))]
    private SimulatorAdvertisedHostKind _selectedHostKind = SimulatorAdvertisedHostKind.AutoLanIp;

    [ObservableProperty]
    private string _detectedLanIp = string.Empty;

    [ObservableProperty]
    private string _customHostInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPortVisible))]
    private PortRangePrivacyPreset _selectedPortPreset = PortRangePrivacyPreset.DefaultRange;

    [ObservableProperty]
    private int _customPortMin = 5100;

    [ObservableProperty]
    private int _customPortMax = 5120;

    [ObservableProperty]
    private string _generatedInvitationUrl = string.Empty;

    public IReadOnlyList<SimulatorAdvertisedHostKind> AvailableHostKinds { get; } = Enum.GetValues<SimulatorAdvertisedHostKind>();
    public IReadOnlyList<PortRangePrivacyPreset> AvailablePortPresets { get; } = Enum.GetValues<PortRangePrivacyPreset>();

    public bool IsCustomHostVisible => SelectedHostKind == SimulatorAdvertisedHostKind.Custom;
    public bool IsCustomPortVisible => SelectedPortPreset == PortRangePrivacyPreset.CustomRange;

    public PersonaInvitationExportFlyoutViewModel(
        ISimulatedPersonaHost host,
        IPeerInvitationCodec codec,
        IStatusToastService toastService,
        string? lanIpOverride = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));

        _detectedLanIp = lanIpOverride ?? SimulatorNetworkHelper.DetectLanIp();
        UpdateGeneratedInvitation();
    }

    partial void OnSelectedHostKindChanged(SimulatorAdvertisedHostKind value) => UpdateGeneratedInvitation();
    partial void OnCustomHostInputChanged(string value) => UpdateGeneratedInvitation();
    partial void OnSelectedPortPresetChanged(PortRangePrivacyPreset value) => UpdateGeneratedInvitation();
    partial void OnCustomPortMinChanged(int value) => UpdateGeneratedInvitation();
    partial void OnCustomPortMaxChanged(int value) => UpdateGeneratedInvitation();

    [RelayCommand]
    public void UpdateGeneratedInvitation()
    {
        var host = SelectedHostKind switch
        {
            SimulatorAdvertisedHostKind.Loopback => "127.0.0.1",
            SimulatorAdvertisedHostKind.Custom => string.IsNullOrWhiteSpace(CustomHostInput) ? "127.0.0.1" : CustomHostInput.Trim(),
            _ => string.IsNullOrWhiteSpace(DetectedLanIp) ? "127.0.0.1" : DetectedLanIp
        };

        PortRange? portRange = SelectedPortPreset switch
        {
            PortRangePrivacyPreset.CustomRange => new PortRange(Math.Min(CustomPortMin, CustomPortMax), Math.Max(CustomPortMin, CustomPortMax)),
            PortRangePrivacyPreset.SinglePort => PortRange.Single(Math.Clamp(_host.BoundPort, 1, 65535)),
            _ => null
        };

        var payload = new PeerInvitationPayload(
            _host.PublicIdentityId,
            _host.MailboxRoutingToken,
            _host.MailboxDeliveryToken,
            new[] { host },
            portRange);

        GeneratedInvitationUrl = _codec.Encode(payload);
    }

    [RelayCommand]
    public async Task CopyInvitationUrlAsync()
    {
        await ClipboardHelper.SetClipboardTextAsync(
            GeneratedInvitationUrl,
            "Invitation URL copied to clipboard.",
            _toastService);
    }
}
