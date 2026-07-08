using System.ComponentModel.DataAnnotations;
using Percolator.Identity;

namespace Percolator.Infrastructure.Identity;

public sealed class PeerVerificationDbo
{
    [Key]
    public long Id { get; set; }

    [Required]
    public PeerId PeerId { get; set; }

    [Required]
    public byte[] Fingerprint { get; set; } = Array.Empty<byte>();

    [Required]
    public int Method { get; set; }

    [Required]
    public DateTimeOffset VerifiedAtUtc { get; set; }

    public string? VerifiedBy { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? NotBeforeUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
