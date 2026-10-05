namespace Percolator.PluginSdk;

public readonly record struct TransferProgress(
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond)
{
    public double FractionComplete => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes, 0.0, 1.0) : 0.0;
}
