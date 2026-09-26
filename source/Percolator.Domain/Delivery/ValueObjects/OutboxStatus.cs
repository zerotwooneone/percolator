namespace Percolator.Domain.Delivery.ValueObjects;

public enum OutboxStatus
{
    Pending = 1,
    InFlight = 2,
    Delivered = 3,
    Failed = 4,
    PausedDormant = 5
}
