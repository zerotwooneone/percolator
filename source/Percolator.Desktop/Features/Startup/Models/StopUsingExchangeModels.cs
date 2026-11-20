namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Input parameters for configuring the "Stop using this Exchange?" dialog.
/// </summary>
public sealed record StopUsingExchangeInput(
    string PersonaName,
    string ExchangeName,
    bool CanPurgeQueue);

/// <summary>
/// Result returned when closing the "Stop using this Exchange?" dialog.
/// </summary>
public sealed record StopUsingExchangeResult(
    bool Confirmed,
    bool PurgeQueuedEnvelopes);
