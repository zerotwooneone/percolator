namespace Percolator.Identity.Model;

/// <summary>
/// Represents a user's stable, long-term identity.
/// </summary>
public record IdentityRecord(SelfId SelfIdentityId, PublicIdentityId PublicIdentityId, DeviceId DeviceId, string Name, string? Nickname = null)
{
    /// <summary>
    /// The listening port for gRPC TLS connections.
    /// Populated from the database SelfIdentityDbo.ListeningPort.
    /// </summary>
    public ListeningPort ListeningPort { get; init; }
}
