namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
    private sealed record GroupSubscriber(
        Guid Id,
        BlindedRoutingToken? RoutingToken,
        ChannelWriter<GroupStreamServerMessage> Writer);

    private readonly IRelayLedgerRepository _ledgerRepository;
    private readonly IZkProofEngine _zkProofEngine;
    private readonly IRelayMailboxCapabilityStore? _mailboxStore;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ChannelId, ConcurrentDictionary<Guid, GroupSubscriber>> _subscribers = new();

    public AnonymousGroupRelayEndpoint(
        IRelayLedgerRepository ledgerRepository,
        IZkProofEngine zkProofEngine,
        IRelayMailboxCapabilityStore? mailboxStore = null,
        IDateTimeProvider? timeProvider = null)
    {
        _ledgerRepository = ledgerRepository ?? throw new ArgumentNullException(nameof(ledgerRepository));
        _zkProofEngine = zkProofEngine ?? throw new ArgumentNullException(nameof(zkProofEngine));
        _mailboxStore = mailboxStore;
        _timeProvider = timeProvider ?? new DefaultDateTimeProvider();
    }

    private static ChannelId ParseConversationId(ByteString conversationId)
    {
        return ChannelId.FromBytes(conversationId.Span);
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
                newTokens.Add(BlindedRoutingToken.FromBytes(tok.Span));
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

        var serverMessage = new GroupStreamServerMessage
        {
            Broadcast = new GroupBroadcastEnvelope
            {
                ConversationId = request.ConversationId,
                Epoch = request.Epoch,
                SenderKeyIteration = request.SenderKeyIteration,
                Ciphertext = request.Ciphertext,
                AuthorSignature = request.AuthorSignature
            }
        };

        var activeTokensWithStreams = new HashSet<BlindedRoutingToken>();
        if (_subscribers.TryGetValue(channelId, out var subs))
        {
            foreach (var sub in subs.Values)
            {
                if (sub.Writer.TryWrite(serverMessage) && sub.RoutingToken.HasValue)
                {
                    activeTokensWithStreams.Add(sub.RoutingToken.Value);
                }
            }
        }

        // Group Outbox / Mailbox fan-out for offline members or members without active streams
        if (_mailboxStore != null && ledger.ActiveRoutingTokens.Count > 0)
        {
            foreach (var token in ledger.ActiveRoutingTokens)
            {
                if (!activeTokensWithStreams.Contains(token))
                {
                    var sealedEnvelope = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = ByteString.CopyFrom(token.ToByteArray()),
                        DeliveryToken = ByteString.Empty,
                        EncryptedPackage = request.Ciphertext,
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(_timeProvider.UtcNow.AddDays(7))
                    };

                    var queued = new QueuedSealedEnvelopeProto
                    {
                        QueueId = Guid.NewGuid().ToString("N"),
                        EnvelopePayload = sealedEnvelope.ToByteString(),
                        EnqueuedAtUtc = Timestamp.FromDateTimeOffset(_timeProvider.UtcNow)
                    };

                    await _mailboxStore.EnqueueEnvelopeAsync(token.ToByteArray(), queued, context.CancellationToken);
                }
            }
        }

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
        BlindedRoutingToken? routingToken = null;
        if (request.SubscriberRoutingToken.Length == 16)
        {
            routingToken = BlindedRoutingToken.FromBytes(request.SubscriberRoutingToken.Span);
        }

        var channelSubs = _subscribers.GetOrAdd(channelId, _ => new ConcurrentDictionary<Guid, GroupSubscriber>());
        channelSubs[subId] = new GroupSubscriber(subId, routingToken, boundedChannel.Writer);

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
            foreach (var sub in subs.Values)
            {
                sub.Writer.TryWrite(message);
            }
        }
    }

    private sealed class DefaultDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
