using Percolator.Domain.Common;

namespace Percolator.PluginSdk;

public interface IPayloadSerializer
{
    ReadOnlyMemory<byte> Serialize<T>(T payload);
    DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data);
}
