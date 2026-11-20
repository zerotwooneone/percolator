namespace Percolator.Apps.FileTransfer.Domain.Exceptions;

public sealed class DomainValidationException : Exception
{
    public DomainValidationException(string message) : base(message)
    {
    }

    public DomainValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
