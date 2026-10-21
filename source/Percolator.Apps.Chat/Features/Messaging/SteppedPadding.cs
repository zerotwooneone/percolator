using System.Security.Cryptography;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Messaging;

public static class SteppedPadding
{
    public static int CalculatePaddedLength(int unpaddedLength)
    {
        if (unpaddedLength <= 256) return 256;
        if (unpaddedLength <= 512) return 512;
        if (unpaddedLength <= 1024) return 1024;
        if (unpaddedLength <= 2048) return 2048;
        if (unpaddedLength <= 4096) return 4096;
        return ((unpaddedLength + 4095) / 4096) * 4096;
    }

    public static ReadOnlyMemory<byte> CreatePaddedEnvelopeBytes<T>(
        IPayloadSerializer serializer,
        ChatPayloadType type,
        T payload)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        var serializedData = serializer.Serialize(payload);
        int paddedLength = CalculatePaddedLength(serializedData.Length);
        int paddingSize = Math.Max(0, paddedLength - serializedData.Length);

        byte[] paddingBytes = paddingSize > 0
            ? RandomNumberGenerator.GetBytes(paddingSize)
            : Array.Empty<byte>();

        var envelope = new ChatPayloadEnvelopeDto(type, serializedData, paddingBytes);
        return serializer.Serialize(envelope);
    }
}
