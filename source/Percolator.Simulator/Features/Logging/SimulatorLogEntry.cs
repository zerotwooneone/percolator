using System;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Logging;

internal sealed record SimulatorLogEntry(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string? NodeName,
    SimulatedNodeId? NodeId,
    string Category,
    string Message,
    string? Exception = null);
