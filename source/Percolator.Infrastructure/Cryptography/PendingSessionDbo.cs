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

        public uint RemotePeerId { get; set; }

        public int ProtocolVersion { get; set; }

        public byte[] Invitation { get; set; } = Array.Empty<byte>();

        public string? RequestCorrelationId { get; set; }

        public bool IsRelayed { get; set; }

        public uint? RelayHostPeerId { get; set; }

        public byte[]? InviterIdentityKey { get; set; }

        public string? CallbackEndpointHost { get; set; }

        public int? CallbackEndpointPort { get; set; }

        public int State { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset? ExpiresAtUtc { get; set; }
    }
}
