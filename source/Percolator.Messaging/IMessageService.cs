using System;
using System.Threading.Tasks;

namespace Percolator.Messaging
{
    /// <summary>
    /// Defines the primary contract for sending and managing messages.
    /// </summary>
    public interface IMessageService
    {
        Task SendDirectMessageAsync(string senderId, string recipientId, string content);
        Task SendGroupMessageAsync(Guid groupId, string senderId, string content);

        Task EditDirectMessageAsync(Guid messageId, string editorId, string newContent);
        Task EditGroupMessageAsync(Guid messageId, string editorId, string newContent);

        Task AnnotateDirectMessageAsync(Guid messageId, string peerId, string emoji);
        Task AnnotateGroupMessageAsync(Guid messageId, string peerId, string emoji);

        Task RemoveDirectMessageAnnotationAsync(Guid messageId, string peerId, string emoji);
        Task RemoveGroupMessageAnnotationAsync(Guid messageId, string peerId, string emoji);
    }
}
