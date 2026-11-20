namespace Percolator.PluginSdk;

public sealed record RelayFileTransferCapabilities(
    bool SupportsTransferPipes,
    bool SupportsSwarmTracker,
    long? MaxPipeBandwidthBytesPerSec,
    int MaxConcurrentPipes);
