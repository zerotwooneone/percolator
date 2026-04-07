namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
