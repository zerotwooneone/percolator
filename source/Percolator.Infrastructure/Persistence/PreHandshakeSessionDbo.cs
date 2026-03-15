namespace Percolator.Infrastructure.Persistence
{
    public class PreHandshakeSessionDbo
    {
        public long Id { get; set; }
        public int SelfIdentityId { get; set; }
        public Guid LocalRequestId { get; set; }
        public byte[] RecipientPublicKeyHash { get; set; } = Array.Empty<byte>();
        public byte[] InitialRootKey { get; set; } = Array.Empty<byte>();

        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public byte[] RemoteIdentityKeySpki { get; set; } = Array.Empty<byte>();
        public byte[]? RemoteIdentityKeySpkiHash { get; set; }

    }
}
