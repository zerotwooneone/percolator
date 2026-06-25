using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Identity;

public sealed class PeerIdentityKeyDbo_V2
{
    [Key]
    public long Id { get; set; }

    [Required]
    public uint PeerId { get; set; }

    [Required]
    public byte[] PublicKeySpki { get; set; } = Array.Empty<byte>();

    [Required]
    public byte[] Fingerprint { get; set; } = Array.Empty<byte>();

    [Required]
    public DateTimeOffset NotBeforeUtc { get; set; }

    [Required]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    [ForeignKey(nameof(PeerId))]
    public PeerIdentityDbo Peer { get; set; } = null!;
}
