using System;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Percolator.Messaging;

namespace Percolator.Application.Messaging
{
    public class MessageService : IMessageService
    {
        private readonly IMessageStore _messageStore;
        private readonly IGroupService _groupService;

        public MessageService(IMessageStore messageStore, IGroupService groupService)
        {
            _messageStore = messageStore;
            _groupService = groupService;
        }

        public async Task SendDirectMessageAsync(string senderId, string recipientId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException("Message content cannot be empty.", nameof(content));
            }
            var message = new DirectMessage(senderId, recipientId, content);
            await _messageStore.StoreDirectMessageAsync(message);
        }

        public async Task SendGroupMessageAsync(Guid groupId, string senderId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException("Message content cannot be empty.", nameof(content));
            }

            var group = await _groupService.GetGroupDetailsAsync(groupId);
            if (group is null || !group.MemberIds.Contains(senderId))
            {
                throw new SecurityException($"User {senderId} is not a member of group {groupId}.");
            }

            var message = new GroupMessage(groupId, senderId, content);
            await _messageStore.StoreGroupMessageAsync(message);
        }

        public async Task EditDirectMessageAsync(Guid messageId, string editorId, string newContent)
        {
            var message = await _messageStore.GetDirectMessageAsync(messageId);
            if (message is null)
            {
                throw new InvalidOperationException($"Message with ID {messageId} not found.");
            }

            if (message.SenderId != editorId)
            {
                throw new SecurityException("Only the sender can edit the message.");
            }

            message.Edit(newContent);

            await _messageStore.UpdateDirectMessageAsync(message);
        }

        public async Task EditGroupMessageAsync(Guid messageId, string editorId, string newContent)
        {
            var message = await _messageStore.GetGroupMessageAsync(messageId);
            if (message is null)
            {
                throw new InvalidOperationException($"Message with ID {messageId} not found.");
            }

            if (message.SenderId != editorId)
            {
                throw new SecurityException("Only the sender can edit the message.");
            }

            message.Edit(newContent);

            await _messageStore.UpdateGroupMessageAsync(message);
        }

        public async Task AnnotateDirectMessageAsync(Guid messageId, string peerId, string emoji)
        {
            if (!AllowedAnnotations.IsAllowed(emoji))
            {
                throw new ArgumentException($"Emoji '{emoji}' is not an allowed annotation.");
            }

            var message = await _messageStore.GetDirectMessageAsync(messageId);
            if (message is null)
            {
                throw new InvalidOperationException($"Message with ID {messageId} not found.");
            }

            if (message.Annotations.Any(a => a.PeerId == peerId && a.Emoji == emoji))
            {
                return;
            }

            message.Annotations.Add(new MessageAnnotation(peerId, emoji));
            await _messageStore.UpdateDirectMessageAsync(message);
        }

        public async Task AnnotateGroupMessageAsync(Guid messageId, string peerId, string emoji)
        {
            if (!AllowedAnnotations.IsAllowed(emoji))
            {
                throw new ArgumentException($"Emoji '{emoji}' is not an allowed annotation.");
            }

            var message = await _messageStore.GetGroupMessageAsync(messageId);
            if (message is null)
            {
                throw new InvalidOperationException($"Message with ID {messageId} not found.");
            }

            var group = await _groupService.GetGroupDetailsAsync(message.GroupId);
            if (group is null || !group.MemberIds.Contains(peerId))
            {
                throw new SecurityException($"User {peerId} is not a member of the group and cannot annotate messages.");
            }

            if (message.Annotations.Any(a => a.PeerId == peerId && a.Emoji == emoji))
            {
                return;
            }

            message.Annotations.Add(new MessageAnnotation(peerId, emoji));
            await _messageStore.UpdateGroupMessageAsync(message);
        }

        public async Task RemoveDirectMessageAnnotationAsync(Guid messageId, string peerId, string emoji)
        {
            var message = await _messageStore.GetDirectMessageAsync(messageId);
            if (message is null)
            {
                return;
            }

            var annotation = message.Annotations.FirstOrDefault(a => a.PeerId == peerId && a.Emoji == emoji);
            if (annotation is not null)
            {
                message.Annotations.Remove(annotation);
                await _messageStore.UpdateDirectMessageAsync(message);
            }
        }

        public async Task RemoveGroupMessageAnnotationAsync(Guid messageId, string peerId, string emoji)
        {
            var message = await _messageStore.GetGroupMessageAsync(messageId);
            if (message is null)
            {
                return;
            }

            var annotation = message.Annotations.FirstOrDefault(a => a.PeerId == peerId && a.Emoji == emoji);
            if (annotation is not null)
            {
                message.Annotations.Remove(annotation);
                await _messageStore.UpdateGroupMessageAsync(message);
            }
        }
    }
}
