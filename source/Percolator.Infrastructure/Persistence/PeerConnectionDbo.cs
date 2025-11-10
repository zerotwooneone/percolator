using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Persistence;

public class PeerConnectionDbo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid PeerId { get; set; } // PK and FK to PeerIdentities.PeerId

    public byte[]? DirectMessagePublicKey { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public ICollection<GrpcEndPointDbo> GrpcEndPoints { get; set; } = new List<GrpcEndPointDbo>();

    public ICollection<TlsCertificateDbo> TlsCertificates { get; set; } = new List<TlsCertificateDbo>();

    /// <summary>
    /// Optional relay peer used to reach this peer (e.g., Host MQ).
    /// </summary>
    public Guid? RelayPeerId { get; set; }
}
