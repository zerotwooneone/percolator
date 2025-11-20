using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Infrastructure.Cryptography
{
    [Table("PendingSessions")]
    public class PendingSessionDbo
    {
        [Key]
        public Guid Id { get; set; }

        public int SelfIdentityId { get; set; }

        public Guid RemotePeerId { get; set; }

        public int ProtocolVersion { get; set; }

        public byte[] Invitation { get; set; } = Array.Empty<byte>();

        public int State { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset? ExpiresAtUtc { get; set; }
    }
}
