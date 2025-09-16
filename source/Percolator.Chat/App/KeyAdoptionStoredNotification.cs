using System;
using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App
{
    public sealed record KeyAdoptionStoredNotification(Guid ConversationId, GroupKeyVersion Version) : INotification;
}
