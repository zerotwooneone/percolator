using System.Buffers;
using Percolator.Domain.Common;

namespace Percolator.PluginSdk;

public interface IPayloadSerializer
{
    void Serialize<T>(T payload, IBufferWriter<byte> writer);
    ReadOnlyMemory<byte> Serialize<T>(T payload);
    DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data);
}
