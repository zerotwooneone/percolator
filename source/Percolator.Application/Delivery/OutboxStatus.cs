namespace Percolator.Application2.Delivery;

public enum OutboxStatus
{
    Pending = 1,
    InFlight = 2,
    Delivered = 3,
    Failed = 4,
    PausedDormant = 5
}
