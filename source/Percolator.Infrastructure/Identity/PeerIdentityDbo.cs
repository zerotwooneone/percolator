using System.ComponentModel.DataAnnotations;
using Percolator.Identity;

namespace Percolator.Infrastructure.Identity;

public sealed class PeerIdentityDbo
{
    [Key]
    public PeerId PeerId { get; set; }
    [Required]
    public PublicIdentityId PublicIdentityId { get; set; }
    [Required]
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public uint PrimaryDeviceId { get; set; } = 1;
    public byte[]? ProfileKey { get; set; }
    public int LastKnownProfileRevision { get; set; }

    public List<PeerIdentityKeyDbo> Keys { get; set; } = new();
    public List<PeerVerificationDbo> Verifications { get; set; } = new();
}
