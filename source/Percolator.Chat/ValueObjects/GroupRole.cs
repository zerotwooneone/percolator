namespace Percolator.Chat.ValueObjects;

/// <summary>
/// The role of a member in a group conversation.
/// </summary>
public enum GroupRole
{
    /// <summary>
    /// Regular member with no administrative privileges.
    /// </summary>
    Standard = 0,
    
    /// <summary>
    /// Admin member with privileges to modify group structure and metadata.
    /// </summary>
    Admin = 1
}
