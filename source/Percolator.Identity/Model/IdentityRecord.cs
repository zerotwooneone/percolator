namespace Percolator.Identity.Model;

/// <summary>
/// Represents a user's stable, long-term identity.
/// </summary>
public record IdentityRecord(Guid Id, string Name, string? Nickname = null)
{
    /// <summary>
    /// Integer surrogate key for scoping data per self-identity.
    /// Populated by the application during identity load.
    /// </summary>
    public SelfId SelfIdentityId { get; init; }
}
