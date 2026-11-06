using System;
using R3;

namespace Percolator.Simulator.Features.Logging;

internal interface ISimulatorLogSink : IDisposable
{
    Observable<SimulatorLogEntry> LogStream { get; }
    void Log(SimulatorLogEntry entry);
}
