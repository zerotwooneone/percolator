namespace Percolator.Grpc.Services;

public class BusyService
{
    private readonly ILogger<BusyService> _logger;
    private readonly Random _random;

    public BusyService(ILogger<BusyService> logger)
    {
        _logger = logger;
        _logger.LogWarning("Using random seed");
        _random = new Random(999);
    }
    
    public double GetDelaySeconds()
    {
        return (_random.NextDouble()*5)+1D;
    }
    
    public DateTimeOffset GetCurrentUtcTime()
    {
        return DateTimeOffset.UtcNow;
    }
}