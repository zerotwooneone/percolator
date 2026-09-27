namespace Percolator.PluginSdk;

public sealed record ApplicationFrame(
    AppId AppId,
    ReadOnlyMemory<byte> Payload);
