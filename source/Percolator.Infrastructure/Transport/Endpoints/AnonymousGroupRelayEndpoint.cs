namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;

public sealed class AnonymousGroupRelayEndpoint : AnonymousGroupRelayService.AnonymousGroupRelayServiceBase
{
    private readonly IRelayLedgerRepository _ledgerRepository;
    private readonly IZkProofEngine _zkProofEngine;
    private readonly IDateTimeProvider _timeProvider;

    public AnonymousGroupRelayEndpoint(
        IRelayLedgerRepository ledgerRepository,
        IZkProofEngine zkProofEngine,
        IDateTimeProvider? timeProvider = null)
    {
        _ledgerRepository = ledgerRepository ?? throw new ArgumentNullException(nameof(ledgerRepository));
        _zkProofEngine = zkProofEngine ?? throw new ArgumentNullException(nameof(zkProofEngine));
        _timeProvider = timeProvider ?? new DefaultDateTimeProvider();
    }

    private static ChannelId ParseConversationId(ByteString conversationId)
    {
        return new ChannelId(new Guid(conversationId.Span, bigEndian: true));
    }

    public override async Task<FetchGroupStateResponse> FetchGroupState(FetchGroupStateRequest request, ServerCallContext context)
    {
        if (request.ConversationId.Length != 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Conversation ID must be 16 bytes."));
        }

        var channelId = ParseConversationId(request.ConversationId);
        var ledger = await _ledgerRepository.GetByChannelIdAsync(channelId, context.CancellationToken);
        if (ledger == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
        }

        return new FetchGroupStateResponse
        {
            CurrentEpoch = ledger.CurrentEpoch.Value,
            EncryptedRosterBlob = ByteString.CopyFrom(ledger.EncryptedRosterBlob.Span)
        };
    }

    public override async Task<CommitRosterMutationResponse> CommitRosterMutation(CommitRosterMutationRequest request, ServerCallContext context)
    {
        if (request.ConversationId.Length != 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Conversation ID must be 16 bytes."));
        }

        var channelId = ParseConversationId(request.ConversationId);
        var ledger = await _ledgerRepository.GetByChannelIdAsync(channelId, context.CancellationToken);
        if (ledger == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
        }

        var newTokens = new HashSet<BlindedRoutingToken>();
        foreach (var tok in request.NewRoutingTokens)
        {
            if (tok.Length == 16)
            {
                newTokens.Add(new BlindedRoutingToken(new Guid(tok.Span, bigEndian: true)));
            }
        }

        var newBlob = EncryptedEntriesBlob.FromSpan(request.NewRosterBlob.Span);
        var proof = ZkPresentationBytes.FromSpan(request.ZkPresentationProof.Span);
        var baseEpoch = new EpochNumber(request.BaseEpoch);

        var result = ledger.CommitMutation(baseEpoch, newBlob, newTokens, proof, _zkProofEngine, _timeProvider);
        if (result.IsFailure)
        {
            if (result.Error!.Code == "EPOCH_CONFLICT")
            {
                throw new RpcException(new Status(StatusCode.Aborted, "EPOCH_CONFLICT"));
            }
            if (result.Error.Code == "INVALID_ZK_PROOF")
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "INVALID_ZK_PROOF"));
            }
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Error.Description));
        }

        await _ledgerRepository.SaveAsync(ledger, context.CancellationToken);

        return new CommitRosterMutationResponse
        {
            Committed = true,
            NewEpoch = ledger.CurrentEpoch.Value
        };
    }

    public override async Task<DispatchGroupMessageResponse> DispatchGroupMessage(DispatchGroupMessageRequest request, ServerCallContext context)
    {
        if (request.ConversationId.Length != 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Conversation ID must be 16 bytes."));
        }

        var channelId = ParseConversationId(request.ConversationId);
        var ledger = await _ledgerRepository.GetByChannelIdAsync(channelId, context.CancellationToken);
        if (ledger == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
        }

        return new DispatchGroupMessageResponse { Accepted = true };
    }

    public override Task SubscribeGroupStream(
        GroupStreamClientMessage request,
        IServerStreamWriter<GroupStreamServerMessage> responseStream,
        ServerCallContext context)
    {
        return Task.CompletedTask;
    }

    private sealed class DefaultDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
