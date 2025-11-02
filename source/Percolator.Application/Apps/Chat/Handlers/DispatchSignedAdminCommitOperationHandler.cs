using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using System;
using System.Linq;
using PeerId = Percolator.Identity.PeerId;
using Percolator.Application.Network;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchSignedAdminCommitOperationHandler : IRequestHandler<DispatchSignedAdminCommitOperationCommand>
{
    private readonly IMediator _mediator;
    private readonly IRemoteEnvelopeSender _sender;
    private readonly IPeerPublicSigningKeyStore _keyStore;
    private readonly ILogger<DispatchSignedAdminCommitOperationHandler> _logger;

    public DispatchSignedAdminCommitOperationHandler(IMediator mediator, IRemoteEnvelopeSender sender, IPeerPublicSigningKeyStore keyStore, ILogger<DispatchSignedAdminCommitOperationHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
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

        var chat = new ChatEnvelope
        {
            Version = 1,
            AdminCommitOperation = commit
        };

        var tasks = request.RecipientPeerIds
            .Where(pid => pid != request.SenderPeerId)
            .Select(pid => ProcessRecipientAsync(pid, chat, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ProcessRecipientAsync(PeerId recipientId, ChatEnvelope chat, CancellationToken cancellationToken)
    {
        try
        {
            byte[]? pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientId, cancellationToken).ConfigureAwait(false);
            await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing signed admin commit op for recipient {RecipientId}", recipientId);
        }
    }
}
