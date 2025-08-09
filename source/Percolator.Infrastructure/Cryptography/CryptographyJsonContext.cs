using Percolator.Cryptography;
using System.Text.Json.Serialization;

namespace Percolator.Infrastructure.Cryptography;

[JsonSourceGenerationOptions(
    WriteIndented = true, 
    Converters = new[] { typeof(SkippedMessageKeyIdentifierJsonConverter) })
]
[JsonSerializable(typeof(DoubleRatchetSession.DoubleRatchetSessionState))]
internal partial class CryptographyJsonContext : JsonSerializerContext
{
}
