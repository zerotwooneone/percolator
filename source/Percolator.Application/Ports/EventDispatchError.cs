namespace Percolator.Application2.Ports;

public sealed record EventDispatchError(
    IApplicationEvent Event,
    Delegate SubscriberHandler,
    Exception Exception);
