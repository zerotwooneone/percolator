using System;
using System.Collections.Generic;
using System.Linq;

namespace Percolator.Messaging
{
    public class GroupMessage
    {
        public Guid Id { get; }
        public Guid GroupId { get; }
        public string SenderId { get; }
        public string Content { get; private set; }
        public DateTime TimestampUtc { get; }
        public bool IsEdited { get; private set; }
        public DateTime? LastEditedTimestampUtc { get; private set; }
        public ICollection<MessageAnnotation> Annotations { get; }

        public GroupMessage(Guid id, Guid groupId, string senderId, string content, DateTime timestampUtc)
        {
            Id = id;
            GroupId = groupId;
            SenderId = senderId;
            Content = content;
            TimestampUtc = timestampUtc;
            Annotations = new List<MessageAnnotation>();
        }

        public GroupMessage(Guid groupId, string senderId, string content) 
            : this(Guid.NewGuid(), groupId, senderId, content, DateTime.UtcNow)
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
