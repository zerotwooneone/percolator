namespace Percolator.Identity.Model;

/// <summary>
/// Represents a user's identity, including their certificate and optional nickname.
/// </summary>
public record IdentityRecord(string Name, PfxCertificate PfxCertificate, string Thumbprint, string? Nickname = null);
