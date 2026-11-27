namespace Percolator.Apps.FileTransfer.Tests.TestDoubles;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

public sealed class TestPayloadSerializer : IPayloadSerializer
{
    private readonly ConcurrentDictionary<string, object> _objectCache = new();

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

        byte[] token = RandomNumberGenerator.GetBytes(16);
        string key = Convert.ToHexString(token);
        _objectCache[key] = payload;
        return token;
    }

    public DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return DomainResult<T>.Failure(new DomainError("PAYLOAD_EMPTY", "Payload data was empty."));
        }

        string key = Convert.ToHexString(data.Span);
        if (_objectCache.TryGetValue(key, out var cached) && cached is T typed)
        {
            return DomainResult<T>.Success(typed);
        }

        return DomainResult<T>.Failure(new DomainError("DESERIALIZATION_FAILED", $"Unable to deserialize payload of type {typeof(T).Name}."));
    }
}
