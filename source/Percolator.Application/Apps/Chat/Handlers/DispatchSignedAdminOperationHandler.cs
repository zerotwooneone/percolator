using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.MessageQueue.Commands;
using System;
using System.Linq;
using PeerId = Percolator.Identity.PeerId;
using AdminOperationKind = Percolator.Chat.Events.AdminOperationKind;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchSignedAdminOperationHandler : IRequestHandler<DispatchSignedAdminOperationCommand>
{
    private readonly IMediator _mediator;
    private readonly ILogger<DispatchSignedAdminOperationHandler> _logger;

    public DispatchSignedAdminOperationHandler(IMediator mediator, ILogger<DispatchSignedAdminOperationHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DispatchSignedAdminOperationCommand request, CancellationToken cancellationToken)
    {
        if (request.RecipientPeerIds.Count == 0)
        {
            _logger.LogInformation("No recipients for signed admin op {OpId}", request.OpId);
            return;
        }

        var payload = new AdminOperationPayload
        {
            Version = 1,
            GroupConversationGuid = ByteString.CopyFrom(request.GroupConversationId.ToByteArray()),
            OpId = ByteString.CopyFrom(request.OpId.ToByteArray()),
            SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(request.SentTimestampUtc),
        };

        switch (request.Kind)
        {
            case AdminOperationKind.GrantAdmin:
                payload.GrantAdmin = new GrantAdmin
                {
                    Version = 1,
                    GranteePublicKey = request.GranteePublicKeySpki is null ? null : ByteString.CopyFrom(request.GranteePublicKeySpki)
                };
                break;
            case AdminOperationKind.RevokeAdmin:
                payload.RevokeAdmin = new RevokeAdmin
                {
                    Version = 1,
                    GranteePublicKey = request.GranteePublicKeySpki is null ? null : ByteString.CopyFrom(request.GranteePublicKeySpki)
                };
                break;
            case AdminOperationKind.UpdateGroupMembership:
                var ugm = new UpdateGroupMembershipPayload { Version = 1 };
                if (request.MembersToAdd != null)
                {
                    ugm.MembersToAdd.AddRange(request.MembersToAdd.Select(g => ByteString.CopyFrom(g.ToByteArray())));
                }
                if (request.MembersToRemove != null)
                {
                    ugm.MembersToRemove.AddRange(request.MembersToRemove.Select(g => ByteString.CopyFrom(g.ToByteArray())));
                }
                if (request.LeaveGroup.HasValue)
                    ugm.LeaveGroup = request.LeaveGroup.Value;
                payload.UpdateGroupMembership = ugm;
                break;
            case AdminOperationKind.UpdateGroupInfo:
                payload.UpdateGroupInfo = new UpdateGroupInfoPayload
                {
                    Version = 1,
                    NewGroupName = request.NewGroupName ?? "",
                    NewGroupAvatar = request.NewGroupAvatar is null ? null : ByteString.CopyFrom(request.NewGroupAvatar)
                };
                break;
            default:
                throw new InvalidOperationException($"Unsupported admin op kind: {request.Kind}");
        }

        if (request.AdminSequenceNumber.HasValue)
            payload.AdminSequenceNumber = request.AdminSequenceNumber.Value;

        var signed = new SignedAdminOperation
        {
            Version = 1,
            Payload = payload,
            Signature = ByteString.CopyFrom(request.Signature)
        };

        var envelope = new InternalEnvelope
        {
            ChatEnvelope = new ChatEnvelope
            {
                Version = 1,
                SignedAdminOperation = signed
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
                _logger.LogError("Failed to enqueue signed admin op for recipient {RecipientId}", recipientId);
                return;
            }

            await _mediator.Send(new Percolator.Application.Network.TryRelayNextForPeerCommand(recipientId), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing signed admin op for recipient {RecipientId}", recipientId);
        }
    }
}
