namespace Percolator.PluginSdk.Events;

public sealed record EventDispatchError(
    IApplicationEvent Event,
    Delegate SubscriberHandler,
    Exception Exception);
