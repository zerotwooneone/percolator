namespace Percolator.PluginSdk;

public interface IPluginActionHandler
{
    AppId TargetAppId { get; }
    ValueTask HandleActionAsync(Guid cardId, string actionId, byte[] customPayload, object? actionContext, CancellationToken ct = default);
}
