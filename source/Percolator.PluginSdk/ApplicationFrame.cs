namespace Percolator.PluginSdk;

public readonly record struct ApplicationFrame(
    AppId AppId,
    ReadOnlyMemory<byte> Payload);
