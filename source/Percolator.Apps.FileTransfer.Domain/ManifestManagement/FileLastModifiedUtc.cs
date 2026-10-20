using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public readonly record struct FileLastModifiedUtc(DateTimeOffset UtcDateTime)
{
    public long UnixSeconds => UtcDateTime.ToUnixTimeSeconds();

    public static FileLastModifiedUtc FromDateTimeOffset(DateTimeOffset dateTimeOffset)
    {
        if (dateTimeOffset.ToUnixTimeSeconds() < 0)
            throw new DomainValidationException("File timestamp cannot precede Unix epoch.");
        return new FileLastModifiedUtc(dateTimeOffset.ToUniversalTime());
    }

    public static FileLastModifiedUtc FromUnixSeconds(long unixSeconds)
    {
        if (unixSeconds < 0)
            throw new DomainValidationException("File timestamp cannot precede Unix epoch.");
        return new FileLastModifiedUtc(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
    }
}
