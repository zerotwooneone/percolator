using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.DiscoverySimulation;

internal sealed partial class DiscoveredPeerItemViewModel : ViewModelBase
{
    private readonly Func<PublicIdentityId, string, Task<bool>> _connectCallback;

    public PublicIdentityId PeerId { get; }
    public string DisplayName { get; }
    public string RelayEndpoint { get; }
    public Bitmap IdenticonBitmap { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isConnected;

    public string ConnectButtonText => IsConnected ? "✓ Connected" : (IsConnecting ? "Connecting..." : "🤝 Connect");

    public DiscoveredPeerItemViewModel(
        PublicIdentityId peerId,
        string displayName,
        string relayEndpoint,
        Func<PublicIdentityId, string, Task<bool>> connectCallback)
    {
        PeerId = peerId;
        DisplayName = displayName;
        RelayEndpoint = relayEndpoint;
        _connectCallback = connectCallback ?? throw new ArgumentNullException(nameof(connectCallback));

        Span<byte> idBytes = stackalloc byte[16];
        peerId.TryWriteBytes(idBytes);
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(idBytes);
        var pngBytes = IdenticonGenerator.CreatePng(seed);
        using var ms = new MemoryStream(pngBytes);
        IdenticonBitmap = new Bitmap(ms);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting || IsConnected) return;

        IsConnecting = true;
        try
        {
            var success = await _connectCallback(PeerId, RelayEndpoint);
            if (success)
            {
                IsConnected = true;
            }
        }
        finally
        {
            IsConnecting = false;
        }
    }
}
