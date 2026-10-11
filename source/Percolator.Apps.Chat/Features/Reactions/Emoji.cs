using System.Globalization;
using Percolator.Domain.Common;

namespace Percolator.Apps.Chat.Features.Reactions;

public readonly record struct Emoji
{
    public const int MaxByteLength = 32;

    public string Value { get; }

    private Emoji(string value)
    {
        Value = value;
    }

    public static DomainResult<Emoji> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DomainResult<Emoji>.Failure(new DomainError("INVALID_EMOJI", "Emoji cannot be empty or whitespace."));
        }

        if (value.Length > MaxByteLength)
        {
            return DomainResult<Emoji>.Failure(new DomainError("INVALID_EMOJI", $"Emoji exceeds maximum length of {MaxByteLength} characters."));
        }

        // Validate that value consists of exactly one extended grapheme cluster
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        if (!enumerator.MoveNext())
        {
            return DomainResult<Emoji>.Failure(new DomainError("INVALID_EMOJI", "Emoji contains no valid text elements."));
        }

        // If there is a second text element, it is more than a single emoji
        if (enumerator.MoveNext())
        {
            return DomainResult<Emoji>.Failure(new DomainError("INVALID_EMOJI", "Emoji must be a single grapheme cluster."));
        }

        return DomainResult<Emoji>.Success(new Emoji(value));
    }

    public override string ToString() => Value ?? string.Empty;
}
