using System.Text;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed record SanitizedRelativePath
{
    public string Value { get; }

    private SanitizedRelativePath(string value) => Value = value;

    public static SanitizedRelativePath Parse(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new DomainValidationException("Relative path cannot be null or whitespace.");

        string normalized = rawPath.Replace('\\', '/');

        if (normalized.StartsWith('/') || (normalized.Length >= 2 && normalized[1] == ':'))
            throw new DomainValidationException($"Path '{rawPath}' is absolute, which is strictly prohibited.");

        string[] segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length > 32)
            throw new DomainValidationException($"Path '{rawPath}' exceeds maximum depth of 32 segments.");

        if (Encoding.UTF8.GetByteCount(normalized) > 1024)
            throw new DomainValidationException($"Path '{rawPath}' exceeds maximum byte length of 1,024 bytes.");

        for (int i = 0; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (string.IsNullOrEmpty(seg) || seg == "." || seg == "..")
                throw new DomainValidationException($"Path '{rawPath}' contains invalid or traversal segment '{seg}'.");

            if (Encoding.UTF8.GetByteCount(seg) > 255)
                throw new DomainValidationException($"Segment '{seg}' exceeds maximum byte length of 255 bytes.");
        }

        return new SanitizedRelativePath(normalized);
    }

    public override string ToString() => Value;
}
