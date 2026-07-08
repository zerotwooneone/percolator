using System.ComponentModel.DataAnnotations;

namespace Percolator.Infrastructure.Persistence;

public class PreKeyBundleDbo
{
    public int Id { get; set; }

    [Required]
    public byte[] PublicKey { get; set; } = null!;

    public uint PeerId { get; set; }

    public ICollection<SignedPreKeyDbo> SignedPreKeys { get; set; } = new List<SignedPreKeyDbo>();
    public ICollection<OneTimePreKeyDbo> OneTimePreKeys { get; set; } = new List<OneTimePreKeyDbo>();
}
