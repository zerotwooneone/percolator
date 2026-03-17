using MediatR;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Network;

public sealed record SentInvitationUpsertedNotification(RequestCorrelationId RequestCorrelationId) : INotification;
