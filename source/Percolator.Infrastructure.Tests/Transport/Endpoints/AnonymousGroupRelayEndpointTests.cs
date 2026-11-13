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
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Tests.Fakes;
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
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

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
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

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
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

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
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

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
            CurrentEpoch = 0,
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
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

    [Test]
    public async Task DispatchGroupMessage_WithOfflineMember_EnqueuesToMailboxStoreWhileStreamingToOnlineMember()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);
        var tokenOnline = BlindedRoutingToken.New();
        var tokenOffline = BlindedRoutingToken.New();

        var initialTokens = new HashSet<BlindedRoutingToken> { tokenOnline, tokenOffline };
        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, initialTokens, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        using var cts = new CancellationTokenSource();
        var streamWriter = new TestServerStreamWriter<GroupStreamServerMessage>();

        var subRequest = new GroupStreamClientMessage
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            CurrentEpoch = 0,
            SubscriberRoutingToken = ByteString.CopyFrom(tokenOnline.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };

        // Start subscription for online member
        var subTask = endpoint.SubscribeGroupStream(subRequest, streamWriter, new TestServerCallContext(cts.Token));

        // Dispatch group message
        var payloadBytes = new byte[] { 101, 102, 103 };
        var dispatchRequest = new DispatchGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            Epoch = 0,
            Ciphertext = ByteString.CopyFrom(payloadBytes),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64]),
            SenderKeyIteration = 42
        };

        var dispatchResponse = await endpoint.DispatchGroupMessage(dispatchRequest, new TestServerCallContext());
        dispatchResponse.Accepted.Should().BeTrue();

        // Let subscriber process
        await Task.Delay(50);
        cts.Cancel();
        await subTask;

        // 1. Online member received via stream
        streamWriter.WrittenMessages.Should().ContainSingle(m =>
            m.PayloadCase == GroupStreamServerMessage.PayloadOneofCase.Broadcast &&
            m.Broadcast.SenderKeyIteration == 42);

        // 2. Offline member had envelope enqueued to mailbox
        var offlineEnvelopes = await mailboxStore.DrainEnvelopesAsync(tokenOffline.ToByteArray());
        offlineEnvelopes.Should().HaveCount(1);
        var sealedEnv = SealedEnvelopeProto.Parser.ParseFrom(offlineEnvelopes[0].EnvelopePayload);
        sealedEnv.EncryptedPackage.ToByteArray().Should().Equal(payloadBytes);

        // 3. Online member was NOT enqueued to mailbox (received via live stream)
        var onlineEnvelopes = await mailboxStore.DrainEnvelopesAsync(tokenOnline.ToByteArray());
        onlineEnvelopes.Should().BeEmpty();
    }

    [Test]
    public async Task CreateGroup_ValidRequest_CreatesGenesisAndSavesLedger()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

        var channelId = ChannelId.New();
        var token1 = BlindedRoutingToken.New();
        var request = new CreateGroupRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            EncryptedGenesisBlob = ByteString.CopyFrom(new byte[128]),
            ZkPublicParams = ByteString.CopyFrom(new byte[64])
        };
        request.InitialRoutingTokens.Add(ByteString.CopyFrom(token1.ToByteArray()));

        var response = await endpoint.CreateGroup(request, new TestServerCallContext());
        response.Created.Should().BeTrue();
        response.CurrentEpoch.Should().Be(0);

        var saved = await repo.GetByChannelIdAsync(channelId);
        saved.Should().NotBeNull();
        saved!.ChannelId.Should().Be(channelId);
        saved.CurrentEpoch.Value.Should().Be(0);
        saved.ActiveRoutingTokens.Should().Contain(token1);
    }

    [Test]
    public async Task CreateGroup_InvalidParams_ThrowsInvalidArgument()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

        var invalidIdReq = new CreateGroupRequest
        {
            ConversationId = ByteString.CopyFrom(new byte[10]),
            EncryptedGenesisBlob = ByteString.CopyFrom(new byte[128]),
            ZkPublicParams = ByteString.CopyFrom(new byte[64])
        };
        var act1 = async () => await endpoint.CreateGroup(invalidIdReq, new TestServerCallContext());
        await act1.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);

        var emptyBlobReq = new CreateGroupRequest
        {
            ConversationId = ByteString.CopyFrom(ChannelId.New().ToByteArray()),
            EncryptedGenesisBlob = ByteString.Empty,
            ZkPublicParams = ByteString.CopyFrom(new byte[64])
        };
        var act2 = async () => await endpoint.CreateGroup(emptyBlobReq, new TestServerCallContext());
        await act2.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);

        var shortParamsReq = new CreateGroupRequest
        {
            ConversationId = ByteString.CopyFrom(ChannelId.New().ToByteArray()),
            EncryptedGenesisBlob = ByteString.CopyFrom(new byte[128]),
            ZkPublicParams = ByteString.Empty
        };
        var act3 = async () => await endpoint.CreateGroup(shortParamsReq, new TestServerCallContext());
        await act3.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);

        var badTokenReq = new CreateGroupRequest
        {
            ConversationId = ByteString.CopyFrom(ChannelId.New().ToByteArray()),
            EncryptedGenesisBlob = ByteString.CopyFrom(new byte[128]),
            ZkPublicParams = ByteString.CopyFrom(new byte[64])
        };
        badTokenReq.InitialRoutingTokens.Add(ByteString.CopyFrom(new byte[10])); // Invalid 10-byte token
        var act4 = async () => await endpoint.CreateGroup(badTokenReq, new TestServerCallContext());
        await act4.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Test]
    public async Task FetchGroupState_ValidZkProof_ReturnsState()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine();
        var timeProvider = new FixedTimeProvider();
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);
        var token = BlindedRoutingToken.New();

        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, new HashSet<BlindedRoutingToken> { token }, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        var request = new FetchGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };

        var response = await endpoint.FetchGroupState(request, new TestServerCallContext());
        response.CurrentEpoch.Should().Be(0);
        response.EncryptedRosterBlob.ToByteArray().Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task FetchGroupState_InvalidZkProof_ThrowsPermissionDenied()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine { ReturnValid = false };
        var timeProvider = new FixedTimeProvider();
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 1, 2, 3 });
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);

        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, genesisBlob, new HashSet<BlindedRoutingToken> { BlindedRoutingToken.New() }, publicParams, timeProvider).Value!;
        await repo.SaveAsync(genesis);

        var shortProofReq = new FetchGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(new byte[32])
        };
        var actShort = async () => await endpoint.FetchGroupState(shortProofReq, new TestServerCallContext());
        await actShort.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);

        var invalidProofReq = new FetchGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };
        var actInvalid = async () => await endpoint.FetchGroupState(invalidProofReq, new TestServerCallContext());
        await actInvalid.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Test]
    public async Task SubscribeGroupStream_InvalidZkProof_ThrowsPermissionDenied()
    {
        var repo = new InMemoryLedgerRepo();
        var zkEngine = new TestZkProofEngine { ReturnValid = false };
        var timeProvider = new FixedTimeProvider();
        var mailboxStore = new FakeRelayMailboxCapabilityStore();
        var endpoint = new AnonymousGroupRelayEndpoint(repo, zkEngine, mailboxStore, timeProvider, PublicIdentityId.New());

        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var genesis = RelayGroupLedger.CreateGenesis(channelId, relayId, EncryptedEntriesBlob.FromSpan(new byte[] { 1 }), new HashSet<BlindedRoutingToken> { BlindedRoutingToken.New() }, ZkGroupPublicParams.FromSpan(new byte[32]), timeProvider).Value!;
        await repo.SaveAsync(genesis);

        var streamWriter = new TestServerStreamWriter<GroupStreamServerMessage>();

        var shortReq = new GroupStreamClientMessage
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(new byte[10])
        };
        var actShort = async () => await endpoint.SubscribeGroupStream(shortReq, streamWriter, new TestServerCallContext());
        await actShort.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);

        var invalidReq = new GroupStreamClientMessage
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(new byte[64])
        };
        var actInvalid = async () => await endpoint.SubscribeGroupStream(invalidReq, streamWriter, new TestServerCallContext());
        await actInvalid.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }
}