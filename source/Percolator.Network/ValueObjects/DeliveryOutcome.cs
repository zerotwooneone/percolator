namespace Percolator.Network.ValueObjects;

public enum DeliveryFailureReason
{
    Timeout,
    Unavailable,
    RateLimited,
    Unauthenticated,
    PermissionDenied,
    Cancelled,
    Backpressure,
    Unknown
}

public sealed class DeliveryOutcome
{
    public bool IsSuccess { get; }
    public DeliveryFailureReason? FailureReason { get; }

    private DeliveryOutcome(bool isSuccess, DeliveryFailureReason? reason)
    {
        IsSuccess = isSuccess;
        FailureReason = reason;
    }

    public static DeliveryOutcome Success() => new(true, null);
    public static DeliveryOutcome Failed(DeliveryFailureReason reason) => new(false, reason);
}
