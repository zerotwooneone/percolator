namespace Percolator.Application.Configuration;

public class TransportOptions
{
    public const string SectionName = "Transport";
    public int SimulatorPort { get; set; }
    public string? AdvertisedHost { get; set; }
}
