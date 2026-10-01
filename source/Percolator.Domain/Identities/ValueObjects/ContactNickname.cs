using System.Text;
using Percolator.Domain.Common;

namespace Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Immutable value object representing a sanitized, displayable peer contact nickname.
/// Invariants:
/// - Maximum length of 64 characters.
/// - Strips all ASCII and Unicode control characters to prevent UI homograph and terminal injection attacks.
/// - Trims leading and trailing whitespace.
/// - If empty or whitespace-only after sanitization, falls back to the peer's public identity ID string.
/// </summary>
public readonly record struct ContactNickname : IEquatable<ContactNickname>
{
    public const int MaxLength = 64;

    public string Value { get; }

    private ContactNickname(string value)
    {
        Value = value;
    }

    public static ContactNickname Create(string? rawInput, PublicIdentityId fallbackIdentityId)
    {
        var sanitized = Sanitize(rawInput);
        if (string.IsNullOrEmpty(sanitized))
        {
            return new ContactNickname(fallbackIdentityId.ToString());
        }

        if (sanitized.Length > MaxLength)
        {
            sanitized = sanitized[..MaxLength];
        }

        return new ContactNickname(sanitized);
    }

    public static DomainResult<ContactNickname> TryCreate(string? rawInput)
    {
        var sanitized = Sanitize(rawInput);
        if (string.IsNullOrEmpty(sanitized))
        {
            return DomainResult<ContactNickname>.Failure(new DomainError(
                "INVALID_NICKNAME", "Contact nickname cannot be empty or contain only control/whitespace characters."));
        }

        if (sanitized.Length > MaxLength)
        {
            return DomainResult<ContactNickname>.Failure(new DomainError(
                "NICKNAME_TOO_LONG", $"Contact nickname cannot exceed {MaxLength} characters."));
        }

        return DomainResult<ContactNickname>.Success(new ContactNickname(sanitized));
    }

    private static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            // Filter out ASCII control chars (0x00-0x1F, 0x7F) and Unicode control / format characters (e.g. zero-width spaces, bidi overrides)
            if (!char.IsControl(ch) && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Trim();
    }

    public override string ToString() => Value;

    public static implicit operator string(ContactNickname nickname) => nickname.Value;
}
