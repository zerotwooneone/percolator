using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class DirectSessionDbo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public PeerId RemotePeerId { get; set; } = null!; // FK to PeerConnections.PeerId

    public Guid SessionId { get; set; }
}
