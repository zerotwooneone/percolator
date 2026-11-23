namespace Percolator.Apps.FileTransfer.Tests.TestDoubles;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

public sealed class TestPayloadSerializer : IPayloadSerializer
{
    private static readonly ConcurrentDictionary<string, object> ObjectCache = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    public void Serialize<T>(T payload, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var bytes = Serialize(payload);
        writer.Write(bytes.Span);
    }

    public ReadOnlyMemory<byte> Serialize<T>(T payload)
    {
        if (payload is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte[] rawBytes;
        try
        {
            rawBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        }
        catch
        {
            rawBytes = Guid.NewGuid().ToByteArray();
        }

        // Cache the instance under the hash of rawBytes so round-tripping returns faithful domain models
        var key = ComputeHash(rawBytes);
        ObjectCache[key] = payload;

        return rawBytes;
    }

    public DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return DomainResult<T>.Failure(new DomainError("PAYLOAD_EMPTY", "Payload data was empty."));
        }

        var key = ComputeHash(data.Span);
        if (ObjectCache.TryGetValue(key, out var cached) && cached is T typed)
        {
            return DomainResult<T>.Success(typed);
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize<T>(data.Span, JsonOptions);
            if (deserialized is not null)
            {
                return DomainResult<T>.Success(deserialized);
            }
        }
        catch
        {
            // Fall through to failure
        }

        return DomainResult<T>.Failure(new DomainError("DESERIALIZATION_FAILED", $"Unable to deserialize payload of type {typeof(T).Name}."));
    }

    private static string ComputeHash(ReadOnlySpan<byte> span)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(span, hash);
        return Convert.ToHexString(hash);
    }
}
