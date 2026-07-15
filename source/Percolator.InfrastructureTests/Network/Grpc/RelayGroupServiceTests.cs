using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Moq;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Cryptography;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Contracts;
using InfrastructureService = Percolator.Infrastructure.Network.Grpc.RelayGroupService;
using Percolator.Infrastructure.Network.Grpc;
using System.Threading.Channels;
using Percolator.Chat.GroupMembership;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.InfrastructureTests.Network.Grpc;

[TestFixture]
public class RelayGroupServiceTests
{
    [Test]
    public async Task Publish_WhenUnauthorized_ThrowsUnauthenticated()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.PublishGroupRelayMessageAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CiphertextBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.Unauthorized);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new SubmitGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            Ciphertext = ByteString.CopyFrom(new byte[] { 0x03, 0x04 }),
            Epoch = 1
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.Publish(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        ex.Which.Status.Detail.Should().Be("Authentication failed.");
    }

    [Test]
    public async Task Publish_WhenEpochConflict_ThrowsAborted()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.PublishGroupRelayMessageAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CiphertextBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.EpochConflict);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new SubmitGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            Ciphertext = ByteString.CopyFrom(new byte[] { 0x03, 0x04 }),
            Epoch = 1
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.Publish(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Aborted);
        ex.Which.Status.Detail.Should().Be("Epoch conflict: client state is stale.");
    }

    [Test]
    public async Task StreamGroupMessages_WhenCallerNotInGroup_ThrowsPermissionDenied()
    {
        // ARRANGE
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        ledgerRepository.Setup(r => r.IsMemberAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<ChatPeerId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>();
        peerIdentityQueries.Setup(q => q.GetPeerIdByPublicIdentityIdAsync(It.IsAny<Percolator.Identity.PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeerId(1));

        var service = new InfrastructureService(
            Mock.Of<IRelayGroupOrchestrator>(),
            ledgerRepository.Object,
            Mock.Of<IRelayGroupStreamDispatcher>(),
            Mock.Of<IPeerIdentityRepository>(),
            peerIdentityQueries.Object);

        var conversationId = Guid.NewGuid();
        var request = new GroupStreamRequest
        {
            ConversationId = ByteString.CopyFrom(conversationId.ToByteArray())
        };

        var headers = new Metadata();
        headers.Add("x-percolator-sender-public-identity-id", Guid.NewGuid().ToString("N"));

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.StreamGroupMessages(request, Mock.Of<IServerStreamWriter<GroupStreamResponse>>(), ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task StreamGroupMessages_WhenCallerIsAuthorized_StartsStreamSuccessfully()
    {
        // ARRANGE
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        ledgerRepository.Setup(r => r.IsMemberAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<ChatPeerId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var dispatcher = new Mock<IRelayGroupStreamDispatcher>();
        var channel = Channel.CreateUnbounded<GroupStreamResponse>();
        var reader = channel.Reader;
        dispatcher.Setup(d => d.RegisterStream(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(reader);

        var peerIdentityQueries = new Mock<IPeerIdentityQueries>();
        peerIdentityQueries.Setup(q => q.GetPeerIdByPublicIdentityIdAsync(It.IsAny<Percolator.Identity.PublicIdentityId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeerId(1));

        var service = new InfrastructureService(
            Mock.Of<IRelayGroupOrchestrator>(),
            ledgerRepository.Object,
            dispatcher.Object,
            Mock.Of<IPeerIdentityRepository>(),
            peerIdentityQueries.Object);

        var conversationId = Guid.NewGuid();
        var request = new GroupStreamRequest
        {
            ConversationId = ByteString.CopyFrom(conversationId.ToByteArray())
        };

        var headers = new Metadata();
        headers.Add("x-percolator-sender-public-identity-id", Guid.NewGuid().ToString("N"));

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None);

        // Mock the response stream writer
        var responseStreamMock = new Mock<IServerStreamWriter<GroupStreamResponse>>();
        responseStreamMock.Setup(s => s.WriteAsync(It.IsAny<GroupStreamResponse>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ACT
        var streamTask = service.StreamGroupMessages(request, responseStreamMock.Object, ctx);
        
        // Complete the channel to signal stream should end
        channel.Writer.Complete();
        
        // ASSERT
        // Public behavior: Stream should complete without throwing when channel completes
        // This verifies authorization passed and stream was registered successfully
        await streamTask;
    }

    [Test]
    public async Task ModifyGroup_WhenValid_CallsOrchestratorAndReturnsSuccess()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.ModifyGroupAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<EncryptedGroupProfileBytes>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.Success);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new ModifyGroupRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            BaseEpoch = 5,
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            NewEncryptedProfile = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };
        request.AddPublicIdentityIds.Add(ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));
        request.RemovePublicIdentityIds.Add(ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var response = await service.ModifyGroup(request, ctx);

        // ASSERT
        response.Success.Should().BeTrue();
    }

    [Test]
    public async Task ModifyGroup_WhenEpochConflict_ThrowsAborted()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.ModifyGroupAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<EncryptedGroupProfileBytes>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.EpochConflict);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new ModifyGroupRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            BaseEpoch = 3,
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            NewEncryptedProfile = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.ModifyGroup(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Aborted);
        ex.Which.Status.Detail.Should().Be("Epoch conflict: client state is stale.");
    }

    [Test]
    public async Task GetGroupState_WhenValid_ReturnsGroupState()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var ledger = new RelayGroupLedger(conversationId, 10, publicParams, encryptedProfile, 1);

        orchestrator.Setup(o => o.GetGroupStateAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupOperationStatus.Success, ledger));

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new GetGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(conversationId.Value.ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x05, 0x06 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var response = await service.GetGroupState(request, ctx);

        // ASSERT
        response.CurrentEpoch.Should().Be(10);
        response.PublicParams.Should().NotBeNull();
        response.EncryptedProfile.Should().NotBeNull();
    }

    [Test]
    public async Task GetGroupState_WhenUnauthorized_ThrowsUnauthenticated()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.GetGroupStateAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupOperationStatus.Unauthorized, null));

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new GetGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.GetGroupState(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task Publish_WhenGroupNotFound_ThrowsNotFound()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.PublishGroupRelayMessageAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CiphertextBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.GroupNotFound);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new SubmitGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            Ciphertext = ByteString.CopyFrom(new byte[] { 0x03, 0x04 }),
            Epoch = 1
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.Publish(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task Publish_WhenSuccess_ReturnsSuccessResponse()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.PublishGroupRelayMessageAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CiphertextBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.Success);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new SubmitGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            Ciphertext = ByteString.CopyFrom(new byte[] { 0x03, 0x04 }),
            Epoch = 1
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var response = await service.Publish(request, ctx);

        // ASSERT
        response.Success.Should().BeTrue();
    }

    [Test]
    public async Task ModifyGroup_WhenUnauthorized_ThrowsUnauthenticated()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.ModifyGroupAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<EncryptedGroupProfileBytes>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.Unauthorized);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new ModifyGroupRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            BaseEpoch = 5,
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            NewEncryptedProfile = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.ModifyGroup(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task ModifyGroup_WhenGroupNotFound_ThrowsNotFound()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.ModifyGroupAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<uint>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<EncryptedGroupProfileBytes>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<IReadOnlyList<Percolator.Identity.PublicIdentityId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RelayGroupOperationStatus.GroupNotFound);

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new ModifyGroupRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            BaseEpoch = 5,
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            NewEncryptedProfile = ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.ModifyGroup(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task GetGroupState_WhenGroupNotFound_ThrowsNotFound()
    {
        // ARRANGE
        var orchestrator = new Mock<IRelayGroupOrchestrator>();
        orchestrator.Setup(o => o.GetGroupStateAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<ZkPresentationBytes>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupOperationStatus.GroupNotFound, null));

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>(), Mock.Of<IPeerIdentityRepository>(), Mock.Of<IPeerIdentityQueries>());

        var request = new GetGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 })
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.GetGroupState(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task StreamGroupMessages_WhenAuthorizationHeaderMissing_ThrowsUnauthenticated()
    {
        // ARRANGE
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var service = new InfrastructureService(
            Mock.Of<IRelayGroupOrchestrator>(),
            ledgerRepository.Object,
            Mock.Of<IRelayGroupStreamDispatcher>(),
            Mock.Of<IPeerIdentityRepository>(),
            Mock.Of<IPeerIdentityQueries>());

        var conversationId = Guid.NewGuid();
        var request = new GroupStreamRequest
        {
            ConversationId = ByteString.CopyFrom(conversationId.ToByteArray())
        };

        var headers = new Metadata();
        // No authorization header added

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: new DateTime(2025, 1, 1, 12, 1, 0, DateTimeKind.Utc),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.StreamGroupMessages(request, Mock.Of<IServerStreamWriter<GroupStreamResponse>>(), ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    private sealed class ServerCallContextStub : ServerCallContext
    {
        private readonly string _peer;
        private readonly DateTime _deadline;
        private readonly Metadata _requestHeaders;
        private readonly CancellationToken _cancellationToken;

        public ServerCallContextStub(string peer, DateTime deadline, Metadata requestHeaders, CancellationToken cancellationToken)
        {
            _peer = peer;
            _deadline = deadline;
            _requestHeaders = requestHeaders;
            _cancellationToken = cancellationToken;
        }

        protected override string MethodCore => "/percolator.contracts.RelayGroupService/Publish";
        protected override string HostCore => "localhost";
        protected override string PeerCore => _peer;
        protected override DateTime DeadlineCore => _deadline;
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _cancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
