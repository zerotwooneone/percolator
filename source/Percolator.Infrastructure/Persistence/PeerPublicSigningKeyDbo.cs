using System.ComponentModel.DataAnnotations;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class PeerPublicSigningKeyDbo
{
    public int Id { get; set; }

    [Required]
    public PeerId PeerId { get; set; } = null!;

    [Required]
    public byte[] PublicKey { get; set; } = null!; // SPKI bytes

    [Required]
    public byte[] PublicKeyHash { get; set; } = null!; // SHA-256 over SPKI

    [Required]
    public DateTimeOffset ActiveAtUtc { get; set; }

    public DateTimeOffset? ExpiredAtUtc { get; set; }
}
