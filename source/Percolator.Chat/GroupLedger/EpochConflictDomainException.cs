namespace Percolator.Chat.GroupLedger;

public sealed class EpochConflictDomainException : DomainException
{
    public EpochConflictDomainException(string message) : base(message)
    {
    }

    public EpochConflictDomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
