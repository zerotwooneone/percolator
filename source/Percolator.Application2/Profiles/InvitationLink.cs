namespace Percolator.Application2.Profiles;

/// <summary>
/// Deep-link URI representation for sharing contact invitations and bootstrap endpoints.
/// Format: percolator://{Host}[:{Port}]/{PublicKey}
/// Port is optional. When omitted, clients probe a sane range of ports or default protocol ports.
/// </summary>
public sealed record InvitationLink
{
    public const string Scheme = "percolator";

    public string Host { get; }
    public int? Port { get; }
    public string PublicKey { get; }

    public InvitationLink(string host, int? port, string publicKey)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535 if specified.");
        }

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new ArgumentException("Public key cannot be empty.", nameof(publicKey));
        }

        Host = host;
        Port = port;
        PublicKey = publicKey.Trim();
    }

    public InvitationLink(string host, string publicKey)
        : this(host, null, publicKey)
    {
    }

    public override string ToString()
    {
        return Port.HasValue
            ? $"{Scheme}://{Host}:{Port.Value}/{PublicKey}"
            : $"{Scheme}://{Host}/{PublicKey}";
    }

    public static InvitationLink Parse(string link)
    {
        if (!TryParse(link, out var result, out var error))
        {
            throw new FormatException(error);
        }

        return result!;
    }

    public static bool TryParse(string? link, out InvitationLink? result, out string? errorMessage)
    {
        result = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(link))
        {
            errorMessage = "Invitation link is empty.";
            return false;
        }

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            errorMessage = "Invitation link is not a valid URI.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Invalid scheme '{uri.Scheme}'. Expected '{Scheme}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            errorMessage = "Host is missing from the invitation link.";
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
        {
            errorMessage = "Public key is missing from the invitation link.";
            return false;
        }

        int? port = uri.Port is > 0 and <= 65535 ? uri.Port : null;

        result = new InvitationLink(uri.Host, port, path);
        return true;
    }
}
