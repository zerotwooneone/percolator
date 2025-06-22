using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Percolator.Identity;

public class ECParametersJsonConverter : JsonConverter<ECParameters>
{
    // Helper record for easy serialization/deserialization of ECParameters fields.
    private record SerializableECParameters(string Curve, byte[] D, ECPoint Q);

    public override ECParameters Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Deserialize into the simple helper record.
        var serializableParams = JsonSerializer.Deserialize<SerializableECParameters>(ref reader, options);
        if (serializableParams is null)
        {
            throw new JsonException("Failed to deserialize ECParameters.");
        }

        // Construct the real ECParameters struct from the helper record's data.
        var ecParams = new ECParameters
        {
            Curve = ECCurve.CreateFromFriendlyName(serializableParams.Curve),
            D = serializableParams.D,
            Q = serializableParams.Q
        };

        ecParams.Validate();
        return ecParams;
    }

    public override void Write(Utf8JsonWriter writer, ECParameters value, JsonSerializerOptions options)
    {
        // Create a serializable helper record from the ECParameters struct.
        var serializableParams = new SerializableECParameters(
            value.Curve.Oid.FriendlyName,
            value.D,
            value.Q
        );

        // Serialize the simple helper record.
        JsonSerializer.Serialize(writer, serializableParams, options);
    }
}

// ECPoint doesn't have a parameterless constructor, so it also needs a converter.
public class ECPointJsonConverter : JsonConverter<ECPoint>
{
    public override ECPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();

        byte[]? x = null, y = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new ECPoint { X = x, Y = y };
            }

            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();

            var propName = reader.GetString();
            reader.Read();

            switch (propName)
            {
                case "X":
                    x = reader.TokenType == JsonTokenType.Null ? null : reader.GetBytesFromBase64();
                    break;
                case "Y":
                    y = reader.TokenType == JsonTokenType.Null ? null : reader.GetBytesFromBase64();
                    break;
            }
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, ECPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.X != null) writer.WriteBase64String("X", value.X);
        if (value.Y != null) writer.WriteBase64String("Y", value.Y);
        writer.WriteEndObject();
    }
}
