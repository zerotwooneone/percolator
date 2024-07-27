using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Grpc.Data;

public class KnownKeys
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    [Timestamp]
    public DateTimeOffset Created { get; set; }
    [StringLength(120)]
    public string? DisplayName { get; set; }
}