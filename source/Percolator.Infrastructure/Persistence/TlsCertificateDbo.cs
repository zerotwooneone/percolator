using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Persistence;

public class TlsCertificateDbo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public Guid PeerId { get; set; }

    public byte[] RawData { get; set; } = Array.Empty<byte>();

    public byte[] RawDataHash { get; set; } = Array.Empty<byte>();

    // navigation
    public PeerConnectionDbo PeerConnection { get; set; } = null!;
}
