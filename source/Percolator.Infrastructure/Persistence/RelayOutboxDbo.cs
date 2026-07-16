namespace Percolator.Infrastructure.Persistence;

/// <summary>
/// Database object for storing pending domain events for processing.
/// Used by the outbox pattern to ensure atomic and resilient event handling.
/// </summary>
public sealed class DomainEventOutboxDbo
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public uint DestinationPeerId { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}
