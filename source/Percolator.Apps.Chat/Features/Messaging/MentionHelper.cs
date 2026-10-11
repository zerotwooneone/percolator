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

        byte[] bytes = Encoding.UTF8.GetBytes(text);
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
