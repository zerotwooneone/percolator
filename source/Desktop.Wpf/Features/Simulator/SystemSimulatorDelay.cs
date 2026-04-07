namespace Desktop.Wpf.Features.Simulator;

public sealed class SystemSimulatorDelay : ISimulatorDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
