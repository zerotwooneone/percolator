using System.ComponentModel.DataAnnotations;

namespace Percolator.Infrastructure.Persistence;

public class PeerPublicSigningKeyDbo
{
    public int Id { get; set; }

    [Required]
    public uint PeerId { get; set; }

    [Required]
    public byte[] PublicKey { get; set; } = null!; // SPKI bytes

    [Required]
    public DateTimeOffset ActiveAtUtc { get; set; }

    public DateTimeOffset? ExpiredAtUtc { get; set; }
}
