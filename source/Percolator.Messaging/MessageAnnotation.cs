using System;

namespace Percolator.Messaging
{
    public class MessageAnnotation
    {
        public string PeerId { get; init; }
        public string Emoji { get; init; }
        public DateTime TimestampUtc { get; init; }

        public MessageAnnotation(string peerId, string emoji)
        {
            PeerId = peerId;
            Emoji = emoji;
            TimestampUtc = DateTime.UtcNow;
        }
    }
}
