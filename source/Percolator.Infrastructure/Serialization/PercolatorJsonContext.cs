using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Percolator.Infrastructure.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ConversationModel))]
[JsonSerializable(typeof(SessionStateModel))]
[JsonSerializable(typeof(List<PeerModel>))]
[JsonSerializable(typeof(ConcurrentDictionary<string, Guid>))]
public partial class PercolatorJsonContext : JsonSerializerContext
{
}
