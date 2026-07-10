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
            .ThrowsAsync(new UnauthorizedDomainException("Invalid credentials"));

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>());

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
        ex.Which.Status.Detail.Should().Be("Invalid credentials");
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
            .ThrowsAsync(new EpochConflictDomainException("Epoch mismatch"));

        var service = new InfrastructureService(orchestrator.Object, Mock.Of<IRelayGroupLedgerRepository>(), Mock.Of<IRelayGroupStreamDispatcher>());

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
        ex.Which.Status.Detail.Should().Be("Epoch mismatch");
    }

    [Test]
    public async Task StreamGroupMessages_WhenCallerNotInGroup_ThrowsPermissionDenied()
    {
        // ARRANGE
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        ledgerRepository.Setup(r => r.IsMemberAsync(
                It.IsAny<ConversationId>(),
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new InfrastructureService(
            Mock.Of<IRelayGroupOrchestrator>(),
            ledgerRepository.Object,
            Mock.Of<IRelayGroupStreamDispatcher>());

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
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var dispatcher = new Mock<IRelayGroupStreamDispatcher>();
        var channel = Channel.CreateUnbounded<GroupStreamResponse>();
        var reader = channel.Reader;
        dispatcher.Setup(d => d.RegisterStream(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(reader);

        var service = new InfrastructureService(
            Mock.Of<IRelayGroupOrchestrator>(),
            ledgerRepository.Object,
            dispatcher.Object);

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
        
        // Wait a moment for stream to start
        await Task.Delay(100);
        
        // Complete the channel to signal stream should end
        channel.Writer.Complete();
        
        // ASSERT
        // Public behavior: Stream should complete without throwing when channel completes
        // This verifies authorization passed and stream was registered successfully
        await streamTask;
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
