namespace Percolator.Identity.Model;

public sealed record DisplayName
{
    public string Value { get; }

    public DisplayName(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Display name cannot be empty or whitespace.", nameof(value));
        if (trimmed.Length > 64) throw new ArgumentException("Display name is too long (max 64).", nameof(value));

        // Allowed: letters, digits, space, hyphen, underscore, apostrophe, dot
        static bool Allowed(char c) => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '\'' || c == '.';
        if (!trimmed.All(Allowed)) throw new ArgumentException("Display name contains invalid characters.", nameof(value));

        Value = trimmed;
    }

    public override string ToString() => Value;
}
