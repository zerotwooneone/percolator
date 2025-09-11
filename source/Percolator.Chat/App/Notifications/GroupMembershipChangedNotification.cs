using System;
using MediatR;

namespace Percolator.Chat.App
{
    public sealed record GroupMembershipChangedNotification(Guid ConversationId) : INotification;
}
