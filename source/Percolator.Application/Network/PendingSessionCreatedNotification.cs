using MediatR;
using Percolator.Cryptography;

namespace Percolator.Application.Network
{
    public sealed record PendingSessionCreatedNotification(PendingSessionId PendingSessionId) : INotification;
}
