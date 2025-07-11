namespace Percolator.Application;

public record InvitationLink(string Host, int Port, string PublicKey)
{
    public override string ToString()
    {
        return $"percolator://{Host}:{Port}/{PublicKey}";
    }

    public static InvitationLink Parse(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            throw new FormatException("Invitation link is not a valid URI.");
        }

        if (uri.Scheme != "percolator")
        {
            throw new FormatException($"Invalid scheme '{uri.Scheme}'. Expected 'percolator'.");
        }

        if (uri.Port == -1)
        {
            throw new FormatException("Port is missing from the invitation link.");
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
        {
            throw new FormatException("Public key is missing from the invitation link.");
        }

        return new InvitationLink(uri.Host, uri.Port, path);
    }
}
