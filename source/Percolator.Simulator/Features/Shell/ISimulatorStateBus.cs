using System;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal interface ISimulatorStateBus : IDisposable
{
    Observable<SimulatorStateSignal> Signals { get; }
    void Publish(SimulatorStateSignal signal);
}

internal sealed class SimulatorStateBus : ISimulatorStateBus
{
    private readonly Subject<SimulatorStateSignal> _subject = new();

    public Observable<SimulatorStateSignal> Signals => _subject;

    public void Publish(SimulatorStateSignal signal)
    {
        _subject.OnNext(signal);
    }

    public void Dispose()
    {
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
