using MediatR;
using Moq;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class InviteHandshakeResponseIngressTests
{
    [Test]
    public async Task HandleAsync_Forwards_to_HandleHandshakeResponderHelloCommand()
    {
        var selfId = new SelfId(1);

        var resp = new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = Guid.NewGuid().ToString(),
            AcceptorIdentityKey = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x01 }),
            AcceptorX3DhEphemeralKey = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x02 }),
            InitialRatchetMessage = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x03, 0x04 })
        };

        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator
            .Setup(m => m.Send(
                It.Is<HandleHandshakeResponderHelloCommand>(c => c.SelfIdentityId == selfId && c.Response == resp),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(Unit.Value))
            .Verifiable();

        var sut = new InviteHandshakeResponseIngress(mediator.Object);

        await sut.HandleAsync(selfId, resp, CancellationToken.None);

        mediator.VerifyAll();
    }
}
