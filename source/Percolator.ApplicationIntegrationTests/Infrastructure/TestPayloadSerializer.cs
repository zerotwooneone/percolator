using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class TestPayloadSerializer : IPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new GuidIdJsonConverter<PublicIdentityId>());
        options.Converters.Add(new GuidIdJsonConverter<ChannelId>());
        options.Converters.Add(new GuidIdJsonConverter<PayloadId>());
        return options;
    }

    public void Serialize<T>(T payload, IBufferWriter<byte> writer)
    {
        var bytes = Serialize(payload);
        writer.Write(bytes.Span);
    }

    public ReadOnlyMemory<byte> Serialize<T>(T payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(payload, Options);
    }

    public DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(data.Span, Options);
            if (result is null)
            {
                return DomainResult<T>.Failure(new DomainError("DESERIALIZATION_FAILED", "Payload deserialized to null."));
            }

            return DomainResult<T>.Success(result);
        }
        catch (Exception ex)
        {
            return DomainResult<T>.Failure(new DomainError("DESERIALIZATION_FAILED", ex.Message));
        }
    }

    private sealed class GuidIdJsonConverter<T> : JsonConverter<T> where T : struct, IParsable<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString();
                if (str is not null && T.TryParse(str, null, out var parsed))
                {
                    return parsed;
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.TryGetProperty("value", out var val) || doc.RootElement.TryGetProperty("Value", out val))
                {
                    var str = val.GetString();
                    if (str is not null && T.TryParse(str, null, out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
