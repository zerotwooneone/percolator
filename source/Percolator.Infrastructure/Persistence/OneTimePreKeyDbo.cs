using System.ComponentModel.DataAnnotations;

namespace Percolator.Infrastructure.Persistence;

public class OneTimePreKeyDbo
{
    [Key]
    public string Id { get; set; } = null!;

    [Required]
    public byte[] PublicKey { get; set; } = null!;

    public int PreKeyBundleId { get; set; }
    public PreKeyBundleDbo PreKeyBundle { get; set; } = null!;
}
