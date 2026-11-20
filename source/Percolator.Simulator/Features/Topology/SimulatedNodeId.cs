using System;

namespace Percolator.Simulator.Features.Topology;

internal readonly record struct SimulatedNodeId
{
    public Guid Value { get; }

    public SimulatedNodeId(Guid value) => Value = value;

    public static SimulatedNodeId CreateNew() => new(Guid.NewGuid());

    public static SimulatedNodeId FromGuid(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("Node ID cannot be empty.", nameof(value))
            : new(value);

    public override string ToString() => Value.ToString("D");
}
