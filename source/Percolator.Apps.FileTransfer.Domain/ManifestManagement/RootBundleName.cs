using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed record RootBundleName
{
    public string Value { get; }

    private RootBundleName(string value) => Value = value;

    public static RootBundleName Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainValidationException("Root bundle name cannot be null or whitespace.");

        string trimmed = value.Trim();
        if (trimmed.Length > 255)
            throw new DomainValidationException("Root bundle name cannot exceed 255 characters.");

        return new RootBundleName(trimmed);
    }

    public override string ToString() => Value;
}
