namespace Percolator.Application.Configuration;

public class TransportOptions
{
    public const string SectionName = "Transport";
    public int GrpcPort { get; set; }
}
