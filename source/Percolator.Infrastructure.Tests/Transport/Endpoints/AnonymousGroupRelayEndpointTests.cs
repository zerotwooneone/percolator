namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport.Endpoints;

[TestFixture]
public sealed class AnonymousGroupRelayEndpointTests
{
    private sealed class InMemoryLedgerRepo : IRelayLedgerRepository
    {
        private readonly ConcurrentDictionary<ChannelId, RelayGroupLedger> _ledgers = new();

        public Task<RelayGroupLedger?> GetByChannelIdAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            _ledgers.TryGetValue(channelId, out var ledger);
            return Task.FromResult(ledger);
        }

        public Task<IReadOnlyList<RelayGroupLedger>> GetAllForRelayAsync(PublicIdentityId relayIdentityId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<RelayGroupLedger> list = _ledgers.Values.Where(l => l.RelayIdentityId == relayIdentityId).ToList();
            return Task.FromResult(list);
        }

        public Task SaveAsync(RelayGroupLedger ledger, CancellationToken cancellationToken = default)
        {
            _ledgers[ledger.ChannelId] = ledger;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        {
            _ledgers.TryRemove(channelId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class TestZkProofEngine : IZkProofEngine
    {
        public bool ReturnValid { get; set; } = true;

        public bool VerifyGroupPresentation(uint epoch, ZkPresentationBytes presentation, ReadOnlySpan<byte> transcriptChallenge, ZkGroupPublicParams publicParams)
        {
            return ReturnValid;
        }

        public ZkPresentationBytes GenerateGroupPresentation(uint epoch, ReadOnlySpan<byte> transcriptChallenge, ReadOnlySpan<byte> groupMasterSecret32, ReadOnlySpan<byte> authCredentialMac)
        {
            return ZkPresentationBytes.FromSpan(new byte[64]);
        }
    }

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public async Task CommitRosterMutation_ValidProofAdvancesEpoch_StaleEpochReturnsConflict()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, timeProvider);

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);
        var token1 = BlindedRoutingToken.New();

        var initialTokens = new HashSet<BlindedRoutingToken> { token1 };
        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, initialTokens, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        // 1. Positive: Valid proof advances epoch from 0 to 1
        var newToken = BlindedRoutingToken.New();
        var mutationRequest = new CommitRosterMutationRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            BaseEpoch = 0,
            NewRosterBlob = ByteString.CopyFrom(new byte[] { 4, 5, 6 }),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };
        mutationRequest.NewRoutingTokens.Add(ByteString.CopyFrom(newToken.ToByteArray()));

        var mutationResponse = await endpoint.CommitRosterMutation(mutationRequest, new TestServerCallContext());
        mutationResponse.Committed.Should().BeTrue();
        mutationResponse.NewEpoch.Should().Be(1);

        var updatedLedger = await repo.GetByChannelIdAsync(channelId);
        updatedLedger!.CurrentEpoch.Value.Should().Be(1);

        // 2. Negative: Stale BaseEpoch = 0 (current is now 1) -> EPOCH_CONFLICT
        var staleRequest = new CommitRosterMutationRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            BaseEpoch = 0, // stale!
            NewRosterBlob = ByteString.CopyFrom(new byte[] { 7, 8, 9 }),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };
        staleRequest.NewRoutingTokens.Add(ByteString.CopyFrom(newToken.ToByteArray()));

        Func<Task> actStale = async () =>
            await endpoint.CommitRosterMutation(staleRequest, new TestServerCallContext());

        var staleEx = await actStale.Should().ThrowAsync<RpcException>();
        staleEx.Which.StatusCode.Should().Be(StatusCode.Aborted);
        staleEx.Which.Status.Detail.Should().Be("EPOCH_CONFLICT");

        // 3. Negative: Invalid ZK proof -> INVALID_ZK_PROOF
        zkEngine.ReturnValid = false;
        var invalidProofRequest = new CommitRosterMutationRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            BaseEpoch = 1, // current epoch
            NewRosterBlob = ByteString.CopyFrom(new byte[] { 7, 8, 9 }),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };
        invalidProofRequest.NewRoutingTokens.Add(ByteString.CopyFrom(newToken.ToByteArray()));

        Func<Task> actInvalid = async () =>
            await endpoint.CommitRosterMutation(invalidProofRequest, new TestServerCallContext());

        var invalidEx = await actInvalid.Should().ThrowAsync<RpcException>();
        invalidEx.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        invalidEx.Which.Status.Detail.Should().Be("INVALID_ZK_PROOF");
    }

    [Test]
    public async Task DispatchGroupMessage_ValidProof_AcceptsAndRejectsReplay()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, timeProvider);

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);
        var token1 = BlindedRoutingToken.New();

        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, new HashSet<BlindedRoutingToken> { token1 }, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        var dispatchRequest = new DispatchGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            Epoch = 0,
            Ciphertext = ByteString.CopyFrom(new byte[] { 10, 20, 30, 40 }),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64]),
            SenderKeyIteration = 1
        };

        // 1. First dispatch succeeds
        var response = await endpoint.DispatchGroupMessage(dispatchRequest, new TestServerCallContext());
        response.Accepted.Should().BeTrue();

        // 2. Replayed dispatch with exact same ciphertext & proof throws AlreadyExists
        Func<Task> actReplay = async () =>
            await endpoint.DispatchGroupMessage(dispatchRequest, new TestServerCallContext());

        var replayEx = await actReplay.Should().ThrowAsync<RpcException>();
        replayEx.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
    }

    [Test]
    public async Task DispatchGroupMessage_InvalidZkProof_ThrowsPermissionDenied()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine { ReturnValid = false };
        var timeProvider = new FixedTimeProvider();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, timeProvider);

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);

        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, new HashSet<BlindedRoutingToken> { BlindedRoutingToken.New() }, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        var dispatchRequest = new DispatchGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            Epoch = 0,
            Ciphertext = ByteString.CopyFrom(new byte[] { 10, 20, 30 }),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };

        Func<Task> actInvalid = async () =>
            await endpoint.DispatchGroupMessage(dispatchRequest, new TestServerCallContext());

        var ex = await actInvalid.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task SubscribeGroupStream_BroadcastsDispatchedMessage()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, timeProvider);

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);

        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, new HashSet<BlindedRoutingToken> { BlindedRoutingToken.New() }, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        using var cts = new CancellationTokenSource();
        var streamWriter = new TestServerStreamWriter<GroupStreamServerMessage>();

        var subRequest = new GroupStreamClientMessage
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            CurrentEpoch = 0
        };

        // Start subscription in background
        var subTask = endpoint.SubscribeGroupStream(subRequest, streamWriter, new TestServerCallContext(cts.Token));

        // Dispatch group message
        var dispatchRequest = new DispatchGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            Epoch = 0,
            Ciphertext = ByteString.CopyFrom(new byte[] { 99, 88, 77 }),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64]),
            SenderKeyIteration = 5
        };

        var dispatchResponse = await endpoint.DispatchGroupMessage(dispatchRequest, new TestServerCallContext());
        dispatchResponse.Accepted.Should().BeTrue();

        // Give reader channel a moment to process and cancel subscriber
        await Task.Delay(50);
        cts.Cancel();
        await subTask;

        streamWriter.WrittenMessages.Should().ContainSingle(m =>
            m.PayloadCase == GroupStreamServerMessage.PayloadOneofCase.Broadcast &&
            m.Broadcast.SenderKeyIteration == 5);
    }
}
