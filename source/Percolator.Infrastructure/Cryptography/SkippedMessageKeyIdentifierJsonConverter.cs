using Percolator.Cryptography;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Percolator.Cryptography.DoubleRatchetSession;

namespace Percolator.Infrastructure.Cryptography;

public class SkippedMessageKeyIdentifierJsonConverter : JsonConverter<SkippedMessageKeyIdentifier>
{
    public override SkippedMessageKeyIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // This is for reading the value from a JSON array, which we don't do for this type.
        throw new NotSupportedException();
    }

    public override void Write(Utf8JsonWriter writer, SkippedMessageKeyIdentifier value, JsonSerializerOptions options)
    {
        // This is for writing the value to a JSON array, which we don't do for this type.
        throw new NotSupportedException();
    }

    public override SkippedMessageKeyIdentifier ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString()!;
        var parts = value.Split(':');
        if (parts.Length != 2 || !ulong.TryParse(parts[1], out var messageNumber))
        {
            throw new JsonException("Invalid format for SkippedMessageKeyIdentifier");
        }

        var publicKey = new RatchetEphemeralKey(Convert.FromBase64String(parts[0]));
        return new SkippedMessageKeyIdentifier(publicKey, messageNumber);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SkippedMessageKeyIdentifier value, JsonSerializerOptions options)
    {
        var keyString = $"{Convert.ToBase64String(value.RatchetKey.Value)}:{value.MessageNumber}";
        writer.WritePropertyName(keyString);
    }
}
