namespace Percolator.Identity;

public readonly record struct DeviceId(uint Value)
{
    public static DeviceId Primary => new(1);
}
