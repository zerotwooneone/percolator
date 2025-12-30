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
    public async Task NonRatchetPayload_returns_failure_and_does_not_call_mediator()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRelayedOpaquePayloadHandler>.Instance;
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var secure = new Mock<Percolator.Application.Services.ISecureMessagingService>(MockBehavior.Strict);
        var lookup = new Mock<IRatchetKeyIndex>(MockBehavior.Strict);
        var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "t", null) { SelfIdentityId = new SelfId(1)} };

        // Not a SessionRatchetMessage; handler should now fail fast.
        var payload = new Percolator.Network.Payload(new byte[] { 0x01, 0x02, 0x03 });
        var relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        var sut = new ProcessRelayedOpaquePayloadHandler(logger, mediator.Object, secure.Object, lookup.Object, activeAccessor, active);
        var result = await sut.Handle(new ProcessRelayedOpaquePayloadCommand(payload, relayHost), CancellationToken.None);

        Assert.That(result.WasSuccess, Is.False);
    }
}
