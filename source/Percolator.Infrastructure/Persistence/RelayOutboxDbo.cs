namespace Percolator.Infrastructure.Persistence;

/// <summary>
/// Database object for storing pending domain events for group provisioning.
/// Used by the outbox pattern to ensure atomic and resilient group creation and invitations.
/// </summary>
public sealed class RelayOutboxDbo
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public uint DestinationPeerId { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}
