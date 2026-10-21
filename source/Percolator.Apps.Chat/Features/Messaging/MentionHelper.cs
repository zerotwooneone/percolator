using System.Buffers;
using System.Text;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public static class MentionHelper
{
    public static MentionDto Create(string text, int charStartIndex, int charLength, PublicIdentityId identityId)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (charStartIndex < 0 || charStartIndex > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(charStartIndex));
        }

        if (charLength < 0 || charStartIndex + charLength > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(charLength));
        }

        int byteStart = Encoding.UTF8.GetByteCount(text.AsSpan(0, charStartIndex));
        int byteLength = Encoding.UTF8.GetByteCount(text.AsSpan(charStartIndex, charLength));

        return new MentionDto(identityId, byteStart, byteLength);
    }

    public static bool IsValidSpan(string text, MentionDto mention)
    {
        if (text is null || mention is null)
        {
            return false;
        }

        if (mention.StartOffset < 0 || mention.Length <= 0)
        {
            return false;
        }

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        Span<byte> utf8Buffer = maxByteCount <= 1024
            ? stackalloc byte[maxByteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

        try
        {
            int actualBytes = Encoding.UTF8.GetBytes(text.AsSpan(), utf8Buffer);
            return IsValidSpanInUtf8Bytes(utf8Buffer.Slice(0, actualBytes), mention);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Filters a list of mentions in a single UTF-8 encoding pass using pooled/stack memory.
    /// Eliminates O(M * N) repeated UTF-8 byte array allocations on the hot path.
    /// </summary>
    public static IReadOnlyList<MentionDto> FilterValidSpans(string? text, IReadOnlyList<MentionDto>? mentions)
    {
        if (string.IsNullOrEmpty(text) || mentions is null || mentions.Count == 0)
        {
            return Array.Empty<MentionDto>();
        }

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        Span<byte> utf8Buffer = maxByteCount <= 1024
            ? stackalloc byte[maxByteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

        try
        {
            int actualBytes = Encoding.UTF8.GetBytes(text.AsSpan(), utf8Buffer);
            ReadOnlySpan<byte> utf8Slice = utf8Buffer.Slice(0, actualBytes);

            var valid = new List<MentionDto>(mentions.Count);
            for (int i = 0; i < mentions.Count; i++)
            {
                var m = mentions[i];
                if (m is not null && IsValidSpanInUtf8Bytes(utf8Slice, m))
                {
                    valid.Add(m);
                }
            }

            return valid;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static bool IsValidSpanInUtf8Bytes(ReadOnlySpan<byte> bytes, MentionDto mention)
    {
        if (mention.StartOffset < 0 || mention.Length <= 0)
        {
            return false;
        }

        if (mention.StartOffset + mention.Length > bytes.Length)
        {
            return false;
        }

        // Must not start in the middle of a multi-byte sequence (continuation byte: 10xxxxxx)
        if (mention.StartOffset > 0 && (bytes[mention.StartOffset] & 0xC0) == 0x80)
        {
            return false;
        }

        // Must not end in the middle of a multi-byte sequence
        int endOffset = mention.StartOffset + mention.Length;
        if (endOffset < bytes.Length && (bytes[endOffset] & 0xC0) == 0x80)
        {
            return false;
        }

        return true;
    }

    public static string ExtractMentionText(string text, MentionDto mention)
    {
        if (!IsValidSpan(text, mention))
        {
            throw new ArgumentException("Invalid mention span for given text.", nameof(mention));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return Encoding.UTF8.GetString(bytes, mention.StartOffset, mention.Length);
    }
}
