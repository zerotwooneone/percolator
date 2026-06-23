namespace Percolator.Chat.GroupLedger;

public sealed class UnauthorizedDomainException : Exception
{
    public UnauthorizedDomainException(string message) : base(message)
    {
    }

    public UnauthorizedDomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
