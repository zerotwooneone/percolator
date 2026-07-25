namespace Percolator.Network.ValueObjects;

/// <summary>
/// Count of delivery attempts for an egress job.
/// </summary>
public readonly record struct DeliveryAttemptCount(int Value)
{
    public override string ToString() => Value.ToString();
}
