using System.Threading;

namespace Desktop.Wpf.Tests;

internal sealed class TestSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);
    public override void Send(SendOrPostCallback d, object? state) => d(state);
}