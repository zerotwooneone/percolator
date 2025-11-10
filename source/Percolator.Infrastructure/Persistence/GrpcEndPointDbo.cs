using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Persistence;

public class GrpcEndPointDbo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public Guid PeerId { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    // navigation
    public PeerConnectionDbo PeerConnection { get; set; } = null!;
}
