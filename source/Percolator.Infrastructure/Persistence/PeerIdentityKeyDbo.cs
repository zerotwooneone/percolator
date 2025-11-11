using System.ComponentModel.DataAnnotations;
using System;

namespace Percolator.Infrastructure.Persistence;

public class PeerIdentityKeyDbo
{
    public int Id { get; set; }

    [Required]
    public byte[] PublicKey { get; set; } = null!;

    public Guid PeerId { get; set; }

    public ICollection<SignedPreKeyDbo> SignedPreKeys { get; set; } = new List<SignedPreKeyDbo>();
    public ICollection<OneTimePreKeyDbo> OneTimePreKeys { get; set; } = new List<OneTimePreKeyDbo>();
}
