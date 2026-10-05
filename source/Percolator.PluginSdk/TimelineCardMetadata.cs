namespace Percolator.PluginSdk;

public sealed record TimelineCardMetadata(
    string IconName,
    string StatusBadge,
    int? ProgressPercent = null,
    long? BytesTransferred = null,
    long? TotalBytes = null,
    double? TransferSpeedBytesPerSec = null);
