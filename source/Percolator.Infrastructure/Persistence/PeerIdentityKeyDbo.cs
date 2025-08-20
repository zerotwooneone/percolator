using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class PeerIdentityKeyDbo
{
    public int Id { get; set; }

    [Required]
    public byte[] PublicKey { get; set; } = null!;

    public PeerId PeerId { get; set; } = null!;
    public Peer Peer { get; set; } = null!;

    public ICollection<SignedPreKeyDbo> SignedPreKeys { get; set; } = new List<SignedPreKeyDbo>();
    public ICollection<OneTimePreKeyDbo> OneTimePreKeys { get; set; } = new List<OneTimePreKeyDbo>();
}
