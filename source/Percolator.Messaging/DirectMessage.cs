using System;
using System.Collections.Generic;
using System.Linq;

namespace Percolator.Messaging
{
    public class DirectMessage
    {
        public Guid Id { get; }
        public string SenderId { get; }
        public string RecipientId { get; }
        public string Content { get; private set; }
        public DateTime TimestampUtc { get; }
        public bool IsEdited { get; private set; }
        public DateTime? LastEditedTimestampUtc { get; private set; }
        public ICollection<MessageAnnotation> Annotations { get; }

        public DirectMessage(Guid id, string senderId, string recipientId, string content, DateTime timestampUtc)
        {
            Id = id;
            SenderId = senderId;
            RecipientId = recipientId;
            Content = content;
            TimestampUtc = timestampUtc;
            Annotations = new List<MessageAnnotation>();
        }

        public DirectMessage(string senderId, string recipientId, string content) 
            : this(Guid.NewGuid(), senderId, recipientId, content, DateTime.UtcNow)
        {
            IsEdited = false;
        }

        public void Edit(string newContent)
        {
            Content = newContent;
            IsEdited = true;
            LastEditedTimestampUtc = DateTime.UtcNow;
        }

        public void AddAnnotation(string peerId, string emoji)
        {
            if (!AllowedAnnotations.IsAllowed(emoji))
            {
                throw new ArgumentException($"Annotation '{emoji}' is not allowed.");
            }

            if (Annotations.Any(a => a.PeerId == peerId && a.Emoji == emoji))
            {
                return; // Annotation already exists
            }

            Annotations.Add(new MessageAnnotation(peerId, emoji));
        }

        public void RemoveAnnotation(string peerId, string emoji)
        {
            var annotation = Annotations.FirstOrDefault(a => a.PeerId == peerId && a.Emoji == emoji);
            if (annotation != null)
            {
                Annotations.Remove(annotation);
            }
        }
    }
}
