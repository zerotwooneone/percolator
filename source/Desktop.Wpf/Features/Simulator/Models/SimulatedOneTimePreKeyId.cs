namespace Desktop.Wpf.Features.Simulator.Models;

public readonly record struct SimulatedOneTimePreKeyId(Guid Value)
{
    public static SimulatedOneTimePreKeyId FromGuid(Guid value) => new(value);
}
