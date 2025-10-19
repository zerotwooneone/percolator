using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.MessageQueue.Commands;
using System;
using System.Linq;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchSignedAdminCommitOperationHandler : IRequestHandler<DispatchSignedAdminCommitOperationCommand>
{
    private readonly IMediator _mediator;
    private readonly ILogger<DispatchSignedAdminCommitOperationHandler> _logger;

    public DispatchSignedAdminCommitOperationHandler(IMediator mediator, ILogger<DispatchSignedAdminCommitOperationHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DispatchSignedAdminCommitOperationCommand request, CancellationToken cancellationToken)
    {
        if (request.RecipientPeerIds.Count == 0)
        {
            _logger.LogInformation("No recipients for signed admin commit op {OpId}", request.OpId);
            return;
        }

        var commit = new SignedAdminCommitOperation
        {
            Version = 1,
            GroupConversationGuid = ByteString.CopyFrom(request.GroupConversationId.ToByteArray()),
            OpId = ByteString.CopyFrom(request.OpId.ToByteArray()),
            CommittedKeyVersion = request.CommittedKeyVersion,
            SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(request.SentTimestampUtc),
            Signature = ByteString.CopyFrom(request.Signature)
        };
        if (request.AdminSequenceNumber.HasValue)
            commit.AdminSequenceNumber = request.AdminSequenceNumber.Value;

        var envelope = new InternalEnvelope
        {
            ChatEnvelope = new ChatEnvelope
            {
                Version = 1,
                AdminCommitOperation = commit
            }
        };
        var bytes = envelope.ToByteArray();

        var tasks = request.RecipientPeerIds
            .Where(pid => pid != request.SenderPeerId)
            .Select(pid => ProcessRecipientAsync(pid, bytes, cancellationToken));

        await Task.WhenAll(tasks);
    }

    private async Task ProcessRecipientAsync(PeerId recipientId, byte[] envelopeBytes, CancellationToken cancellationToken)
    {
        try
        {
            var recipientKeyHash = recipientId.Value.ToByteArray();
            var enqueueResult = await _mediator.Send(
                new EnqueueOpaqueMessageCommand(recipientKeyHash, envelopeBytes),
                cancellationToken);

            if (!enqueueResult.Accepted)
            {
                _logger.LogError("Failed to enqueue signed admin commit op for recipient {RecipientId}", recipientId);
                return;
            }

            await _mediator.Send(new Percolator.Application.Network.TryRelayNextForPeerCommand(recipientId), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing signed admin commit op for recipient {RecipientId}", recipientId);
        }
    }
}
