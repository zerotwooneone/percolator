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

        var service = new InfrastructureService(orchestrator.Object);

        var request = new SubmitGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            Ciphertext = ByteString.CopyFrom(new byte[] { 0x03, 0x04 }),
            Epoch = 1
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: DateTime.UtcNow.AddMinutes(1),
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

        var service = new InfrastructureService(orchestrator.Object);

        var request = new SubmitGroupMessageRequest
        {
            ConversationId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Presentation = ByteString.CopyFrom(new byte[] { 0x01, 0x02 }),
            Ciphertext = ByteString.CopyFrom(new byte[] { 0x03, 0x04 }),
            Epoch = 1
        };

        var ctx = new ServerCallContextStub(
            peer: "ipv4:127.0.0.1:7777",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None);

        // ACT
        var act = async () => await service.Publish(request, ctx);

        // ASSERT
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Aborted);
        ex.Which.Status.Detail.Should().Be("Epoch mismatch");
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
