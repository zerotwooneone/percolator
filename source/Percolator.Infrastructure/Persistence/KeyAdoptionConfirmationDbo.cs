namespace Percolator.Infrastructure.Persistence
{
    public class KeyAdoptionConfirmationDbo
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public uint KeyVersion { get; set; }
        public byte[] AdopterIdentityKey { get; set; } = null!;
        public byte[] Signature { get; set; } = null!;
        public DateTimeOffset SentAtUtc { get; set; }
    }
}
