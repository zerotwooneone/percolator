using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Percolator.Infrastructure.Identity;

public sealed class PeerIdentityDbo
{
    [Key]
    public Guid PeerId { get; set; }
    [Required]
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<PeerIdentityKeyDbo_V2> Keys { get; set; } = new();
    public List<PeerVerificationDbo> Verifications { get; set; } = new();
}
