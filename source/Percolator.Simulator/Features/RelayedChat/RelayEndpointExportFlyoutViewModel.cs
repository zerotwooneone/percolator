using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed partial class RelayEndpointExportFlyoutViewModel : ViewModelBase
{
    private readonly ISimulatedRelayHost _relayHost;
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomHostVisible))]
    private SimulatorAdvertisedHostKind _selectedHostKind = SimulatorAdvertisedHostKind.AutoLanIp;

    [ObservableProperty]
    private string _detectedLanIp = string.Empty;

    [ObservableProperty]
    private string _customHostInput = string.Empty;

    [ObservableProperty]
    private string _generatedRelayUrl = string.Empty;

    public bool IsCustomHostVisible => SelectedHostKind == SimulatorAdvertisedHostKind.Custom;

    public IReadOnlyList<SimulatorAdvertisedHostKind> AvailableHostKinds { get; } = Enum.GetValues<SimulatorAdvertisedHostKind>();

    public RelayEndpointExportFlyoutViewModel(
        ISimulatedRelayHost relayHost,
        IStatusToastService toastService,
        string? lanIpOverride = null)
    {
        _relayHost = relayHost ?? throw new ArgumentNullException(nameof(relayHost));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));

        _detectedLanIp = lanIpOverride ?? SimulatorNetworkHelper.DetectLanIp();
        UpdateGeneratedRelayUrl();
    }

    partial void OnSelectedHostKindChanged(SimulatorAdvertisedHostKind value) => UpdateGeneratedRelayUrl();
    partial void OnCustomHostInputChanged(string value) => UpdateGeneratedRelayUrl();

    [RelayCommand]
    public void UpdateGeneratedRelayUrl()
    {
        var host = SelectedHostKind switch
        {
            SimulatorAdvertisedHostKind.Loopback => "127.0.0.1",
            SimulatorAdvertisedHostKind.Custom => string.IsNullOrWhiteSpace(CustomHostInput) ? "127.0.0.1" : CustomHostInput.Trim(),
            _ => string.IsNullOrWhiteSpace(DetectedLanIp) ? "127.0.0.1" : DetectedLanIp
        };

        var formattedHost = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        GeneratedRelayUrl = $"https://{formattedHost}:{_relayHost.BoundPort}";
    }

    [RelayCommand]
    public async Task CopyRelayUrlAsync()
    {
        await ClipboardHelper.SetClipboardTextAsync(
            GeneratedRelayUrl,
            "Relay endpoint URL copied to clipboard.",
            _toastService);
    }
}
