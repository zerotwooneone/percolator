namespace Percolator.Application.ReverseSignal;

public sealed class ReverseSignalOptions
{
    public const string SectionName = "ReverseSignal";

    public bool AllowLoopback { get; set; }
    public bool AllowLan { get; set; }
}
