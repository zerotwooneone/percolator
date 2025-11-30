using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class ProcessRelayedOpaquePayloadCommandTests
{
    [Test]
    public async Task PlaintextHello_threads_RelayHostPeerId_and_sends_responder_cipher()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "t", null) { SelfIdentityId = new SelfId(1)} };
        var msgSvc = new Mock<Percolator.Application.Network.IMessageService>(MockBehavior.Strict);

        // Build a valid plaintext hello envelope (no DR wrapper)
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
            InitiatorEphemeralKeySpki = Google.Protobuf.ByteString.CopyFrom(new byte[] { 4, 5, 6 }),
            SignedPreKeyId = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
        };
        var payload = new Percolator.Network.Payload(hello.ToByteArray());
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        // When mediator receives HandleHandshakeInitiatorHelloCommand, return a cipher and remote id
        mediator.Setup(m => m.Send(It.IsAny<HandleHandshakeInitiatorHelloCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) =>
            {
                var c = (HandleHandshakeInitiatorHelloCommand)cmd;
                Assert.That(c.RelayHostPeerId, Is.EqualTo(relayHost));
            })
            .ReturnsAsync(new HandleHandshakeInitiatorHelloResult(new Percolator.Identity.PeerId(Guid.NewGuid()), new SessionRatchetMessage(new byte[] { 9 })));

        // MessageService expected to send the pre-encrypted cipher
        msgSvc.Setup(s => s.SendPreEncryptedAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Percolator.Application.Network.SendResult.CreateSuccess("Direct", new[] { "Direct" }, 1));

        var sut = new ProcessRelayedOpaquePayloadHandler(logger, mediator.Object, secure.Object, lookup.Object, active, msgSvc.Object);
        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.True);
    }
}
