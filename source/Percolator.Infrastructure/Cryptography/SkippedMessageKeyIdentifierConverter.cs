using System.Text.Json;
using System.Text.Json.Serialization;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

/// <summary>
/// JSON converter for SkippedMessageKeyIdentifier to allow it to be used as a dictionary key.
/// </summary>
public class SkippedMessageKeyIdentifierConverter : JsonConverter<SkippedMessageKeyIdentifier>
{
    public override SkippedMessageKeyIdentifier? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected string value for SkippedMessageKeyIdentifier");
        }

        string? value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // Format is "base64Key:counter"
        var parts = value.Split(':');
        if (parts.Length != 2)
        {
            throw new JsonException("Invalid SkippedMessageKeyIdentifier format");
        }

        var keyBytes = Convert.FromBase64String(parts[0]);
        var counter = ulong.Parse(parts[1]);
        
        return new SkippedMessageKeyIdentifier(new RatchetEphemeralKey(keyBytes), counter);
    }

    public override void Write(Utf8JsonWriter writer, SkippedMessageKeyIdentifier value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // Format as "base64Key:counter"
        string serialized = $"{Convert.ToBase64String(value.RatchetKey.Value)}:{value.MessageNumber}";
        writer.WriteStringValue(serialized);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SkippedMessageKeyIdentifier value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WritePropertyName("null");
            return;
        }

        // Format as "base64Key:counter" for property name
        string serialized = $"{Convert.ToBase64String(value.RatchetKey.Value)}:{value.MessageNumber}";
        writer.WritePropertyName(serialized);
    }

    public override SkippedMessageKeyIdentifier ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string propertyName = reader.GetString() ?? throw new JsonException("Property name cannot be null");
        
        if (propertyName == "null")
        {
            return null!;
        }

        // Format is "base64Key:counter"
        var parts = propertyName.Split(':');
        if (parts.Length != 2)
        {
            throw new JsonException("Invalid SkippedMessageKeyIdentifier format in property name");
        }

        var keyBytes = Convert.FromBase64String(parts[0]);
        var counter = ulong.Parse(parts[1]);
        
        return new SkippedMessageKeyIdentifier(new RatchetEphemeralKey(keyBytes), counter);
    }
}
