using MediatR;

namespace Percolator.Application.Apps.Chat
{
    // Published after a local successful group key import and persistence
    public sealed record KeyVersionAdoptedNotification(Guid ConversationId, uint KeyVersion) : INotification;
}
