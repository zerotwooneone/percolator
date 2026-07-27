namespace Percolator.Infrastructure.Network.RelayLedger;

/// <summary>
/// Database object for storing blinded roster entries for relay fan-out.
/// The Relay remains completely blinded to the true identity of the RoutingToken.
/// No foreign key relationships to identity tables are maintained.
/// </summary>
public sealed class RelayBlindedRosterDbo
{
    public Guid ConversationId { get; set; }
    public byte[] RoutingToken { get; set; } = Array.Empty<byte>();
    public DateTimeOffset AddedAtUtc { get; set; }
}
