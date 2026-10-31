namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
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
    private readonly ConcurrentDictionary<ChannelId, ConcurrentDictionary<Guid, ChannelWriter<GroupStreamServerMessage>>> _subscribers = new();

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

        BroadcastMessage(channelId, new GroupStreamServerMessage
        {
            Mutation = new EpochMutationNotification
            {
                ConversationId = request.ConversationId,
                NewEpoch = ledger.CurrentEpoch.Value
            }
        });

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

        if (request.Ciphertext.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Group dispatch ciphertext cannot be empty."));
        }

        var channelId = ParseConversationId(request.ConversationId);
        var ledger = await _ledgerRepository.GetByChannelIdAsync(channelId, context.CancellationToken);
        if (ledger == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Group not found."));
        }

        var proof = ZkPresentationBytes.FromSpan(request.ZkPresentationProof.Span);
        var verifyResult = ledger.VerifyDispatch(proof, request.Ciphertext.Span, _zkProofEngine);
        if (verifyResult.IsFailure)
        {
            if (verifyResult.Error!.Code == "DISPATCH_REPLAY_DETECTED")
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, verifyResult.Error.Description));
            }
            if (verifyResult.Error.Code == "INVALID_ZK_PROOF")
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, verifyResult.Error.Description));
            }
            if (verifyResult.Error.Code == "EMPTY_CIPHERTEXT")
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, verifyResult.Error.Description));
            }
            throw new RpcException(new Status(StatusCode.InvalidArgument, verifyResult.Error.Description));
        }

        await _ledgerRepository.SaveAsync(ledger, context.CancellationToken);

        BroadcastMessage(channelId, new GroupStreamServerMessage
        {
            Broadcast = new GroupBroadcastEnvelope
            {
                ConversationId = request.ConversationId,
                Epoch = request.Epoch,
                SenderKeyIteration = request.SenderKeyIteration,
                Ciphertext = request.Ciphertext,
                AuthorSignature = request.AuthorSignature
            }
        });

        return new DispatchGroupMessageResponse { Accepted = true };
    }

    public override async Task SubscribeGroupStream(
        GroupStreamClientMessage request,
        IServerStreamWriter<GroupStreamServerMessage> responseStream,
        ServerCallContext context)
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

        var boundedChannel = Channel.CreateBounded<GroupStreamServerMessage>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var subId = Guid.NewGuid();
        var channelSubs = _subscribers.GetOrAdd(channelId, _ => new ConcurrentDictionary<Guid, ChannelWriter<GroupStreamServerMessage>>());
        channelSubs[subId] = boundedChannel.Writer;

        try
        {
            while (await boundedChannel.Reader.WaitToReadAsync(context.CancellationToken))
            {
                while (boundedChannel.Reader.TryRead(out var msg))
                {
                    await responseStream.WriteAsync(msg);
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Client closed stream normally
        }
        finally
        {
            channelSubs.TryRemove(subId, out _);
        }
    }

    private void BroadcastMessage(ChannelId channelId, GroupStreamServerMessage message)
    {
        if (_subscribers.TryGetValue(channelId, out var subs))
        {
            foreach (var writer in subs.Values)
            {
                writer.TryWrite(message);
            }
        }
    }

    private sealed class DefaultDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
