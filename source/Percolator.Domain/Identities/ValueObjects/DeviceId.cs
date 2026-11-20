namespace Percolator.Domain.Identities.ValueObjects;

public readonly record struct DeviceId(uint Value) : IEquatable<DeviceId>
{
    public static DeviceId Primary => new(1);

    public bool IsPrimary => Value == 1;
    public bool IsValid => Value > 0;

    public override string ToString() => Value.ToString();
}
