using MediatR;
using Percolator.MessageQueue.Results;

namespace Percolator.MessageQueue.Commands;

public record EnqueueOpaqueMessageCommand(
    byte[] RecipientPublicKeyHash,
    byte[] MessageBlob
) : IRequest<EnqueueOpaqueMessageResult>;
