namespace Percolator.Application.Ports;

public sealed record EventDispatchError(
    IApplicationEvent Event,
    Delegate SubscriberHandler,
    Exception Exception);
