namespace Percolator.Infrastructure.Network.Certificates;

public class TlsOptions
{
    public const string SectionName = "Tls";
    
    public PortRangeOptions PortRange { get; set; } = new();
    public CertificateOptions Certificate { get; set; } = new();
    public RetryPolicyOptions RetryPolicy { get; set; } = new();
}

public class PortRangeOptions
{
    public int Start { get; set; } = 50000;
    public int End { get; set; } = 50100;
}

public class CertificateOptions
{
    public int MaxAgeDays { get; set; } = 90;
    public string Password { get; set; } = "percolator-node-transport";
}

public class RetryPolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public int InitialDelayMs { get; set; } = 100;
    public double BackoffMultiplier { get; set; } = 2.0;
}
