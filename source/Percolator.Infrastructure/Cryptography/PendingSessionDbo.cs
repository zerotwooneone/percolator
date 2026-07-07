using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Percolator.Identity;

namespace Percolator.Infrastructure.Cryptography
{
    [Table("PendingSessions")]
    public class PendingSessionDbo
    {
        [Key]
        public Guid Id { get; set; }

        public SelfId SelfIdentityId { get; set; }

        public PeerId RemotePeerId { get; set; }

        public int ProtocolVersion { get; set; }

        public byte[] Invitation { get; set; } = Array.Empty<byte>();

        public string? RequestCorrelationId { get; set; }

        public bool IsRelayed { get; set; }

        public PeerId? RelayHostPeerId { get; set; }

        public byte[]? InviterIdentityKey { get; set; }

        public string? CallbackEndpointHost { get; set; }

        public int? CallbackEndpointPort { get; set; }

        public int State { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset? ExpiresAtUtc { get; set; }
    }
}
