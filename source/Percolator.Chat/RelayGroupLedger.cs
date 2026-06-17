using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Chat;

/// <summary>
/// Domain aggregate root for the Relay's authoritative encrypted ledger.
/// Enforces optimistic concurrency via epoch validation.
/// </summary>
public sealed class RelayGroupLedger
{
    public ConversationId ConversationId { get; }
    public uint CurrentEpoch { get; private set; }
    public ZkGroupPublicParamsBytes GroupPublicParams { get; }

    public RelayGroupLedger(ConversationId conversationId, uint currentEpoch, ZkGroupPublicParamsBytes groupPublicParams)
    {
        ConversationId = conversationId;
        CurrentEpoch = currentEpoch;
        GroupPublicParams = groupPublicParams;
    }

    /// <summary>
    /// Advances the epoch if the requested epoch is greater than the current epoch.
    /// Throws if the requested epoch is stale (less than or equal to current).
    /// </summary>
    public void AdvanceEpoch(uint requestedEpoch)
    {
        if (requestedEpoch <= CurrentEpoch)
        {
            throw new StaleEpochDomainException(CurrentEpoch);
        }

        CurrentEpoch = requestedEpoch;
    }
}

/// <summary>
/// Domain exception thrown when attempting to advance to a stale epoch.
/// </summary>
public sealed class StaleEpochDomainException : Exception
{
    public uint CurrentEpoch { get; }

    public StaleEpochDomainException(uint currentEpoch)
        : base($"Requested epoch is not greater than current epoch {currentEpoch}.")
    {
        CurrentEpoch = currentEpoch;
    }
}

/// <summary>
/// Domain exception thrown when a concurrency conflict occurs during ledger save.
/// </summary>
public sealed class EpochConflictDomainException : Exception
{
    public uint WinningEpoch { get; }

    public EpochConflictDomainException(uint winningEpoch)
        : base($"Concurrency conflict: another process advanced the ledger to epoch {winningEpoch}.")
    {
        WinningEpoch = winningEpoch;
    }
}

/// <summary>
/// Domain exception thrown when attempting to save a ledger that was deleted.
/// </summary>
public sealed class LedgerDeletedDomainException : Exception
{
    public LedgerDeletedDomainException()
        : base("The ledger was deleted by another process.")
    {
    }
}
