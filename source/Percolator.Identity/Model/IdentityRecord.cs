namespace Percolator.Identity.Model;

/// <summary>
/// Represents a user's stable, long-term identity.
/// </summary>
public record IdentityRecord(Guid Id, string Name, string? Nickname = null);
