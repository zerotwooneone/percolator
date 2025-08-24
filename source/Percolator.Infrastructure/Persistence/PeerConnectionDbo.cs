using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class PeerConnectionDbo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public PeerId PeerId { get; set; } = null!; // PK and FK to Peers.Id

    public byte[]? DirectMessagePublicKey { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public ICollection<GrpcEndPointDbo> GrpcEndPoints { get; set; } = new List<GrpcEndPointDbo>();

    public ICollection<TlsCertificateDbo> TlsCertificates { get; set; } = new List<TlsCertificateDbo>();
}
