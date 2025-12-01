using System;
using MediatR;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Chat.App.Notifications;

public sealed record PendingHandshakeAdded(
    PendingSessionId Id,
    PeerId RemotePeerId,
    DateTimeOffset CreatedAtUtc,
    string? DisplayName
) : INotification;
