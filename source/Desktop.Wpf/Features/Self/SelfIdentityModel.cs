using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Features.Self;

public sealed record SelfIdentityModel
{
    public string DisplayName { get; }
    public string Initials { get; }
    public SelfId Id { get; }
    public ListeningPort ListeningPort { get; }
    public bool Active { get; }

    public SelfIdentityModel(SelfId selfId, string displayName, ListeningPort listeningPort, bool active=false)
    {
        Id = selfId;
        DisplayName = displayName;
        Initials = ComputeInitials(displayName);
        ListeningPort = listeningPort;
        Active = active;
    }
    
    public static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
