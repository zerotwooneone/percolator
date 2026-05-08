using MediatR;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;

namespace Percolator.Application.Network;

public sealed record PendingSessionRemovedNotification(
    PendingSessionId PendingSessionId,
    RequestCorrelationId RequestCorrelationId,
    PendingSessionRemoveReason Reason) : INotification;
