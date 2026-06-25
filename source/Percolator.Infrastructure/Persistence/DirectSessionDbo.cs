using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Persistence;

public class DirectSessionDbo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public uint RemotePeerId { get; set; } // FK to PeerConnections.PeerId

    public Guid SessionId { get; set; }
    public int SelfIdentityId { get; set; }
}
