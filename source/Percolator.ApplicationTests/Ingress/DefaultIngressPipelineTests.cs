using MediatR;
using Moq;
using Percolator.Application.Ingress;
using Percolator.Application.Network;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Ingress;

[TestFixture]
public class DefaultIngressPipelineTests
{
    [Test]
    public async Task NotReady_Returns_Rejected_NotReady_And_DoesNotDispatch()
    {
        var readiness = new Mock<IIngressReadinessGate>(MockBehavior.Strict);
        readiness.Setup(r => r.EnsureReady()).Throws(new InvalidOperationException("not ready"));

        var validator = new Mock<IIngressValidator>(MockBehavior.Strict);
        var mediator = new Mock<IMediator>(MockBehavior.Strict);

        var sut = new DefaultIngressPipeline(readiness.Object, validator.Object, mediator.Object);

        var result = await sut.DeliverOpaqueAsync(new IngressOpaquePayload(new byte[] { 0x01 }, new SelfId(1)), CancellationToken.None);

        Assert.That(result.Disposition, Is.EqualTo(IngressDisposition.Rejected_NotReady));
        mediator.VerifyNoOtherCalls();
        validator.VerifyNoOtherCalls();
    }

    [Test]
    public async Task InvalidPayload_Returns_Rejected_Invalid_And_DoesNotDispatch()
    {
        var readiness = new Mock<IIngressReadinessGate>(MockBehavior.Strict);
        readiness.Setup(r => r.EnsureReady());

        var validator = new Mock<IIngressValidator>(MockBehavior.Strict);
        validator.Setup(v => v.Validate(It.IsAny<IngressOpaquePayload>())).Throws(new InvalidOperationException("invalid"));

        var mediator = new Mock<IMediator>(MockBehavior.Strict);

        var sut = new DefaultIngressPipeline(readiness.Object, validator.Object, mediator.Object);

        var result = await sut.DeliverOpaqueAsync(new IngressOpaquePayload(Array.Empty<byte>(), new SelfId(1)), CancellationToken.None);

        Assert.That(result.Disposition, Is.EqualTo(IngressDisposition.Rejected_Invalid));
        mediator.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ValidPayload_Dispatches_And_Returns_Accepted_With_ResponseBytes()
    {
        var readiness = new Mock<IIngressReadinessGate>(MockBehavior.Strict);
        readiness.Setup(r => r.EnsureReady());

        var validator = new Mock<IIngressValidator>(MockBehavior.Strict);
        validator.Setup(v => v.Validate(It.IsAny<IngressOpaquePayload>()));

        var expected = new byte[] { 0xAA, 0xBB };

        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator
            .Setup(m => m.Send(It.IsAny<DeliverOpaqueMessageCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliverOpaqueMessageResult { ResponsePayloadBytes = expected });

        var sut = new DefaultIngressPipeline(readiness.Object, validator.Object, mediator.Object);

        var result = await sut.DeliverOpaqueAsync(new IngressOpaquePayload(new byte[] { 0x01, 0x02 }, new SelfId(1)), CancellationToken.None);

        Assert.That(result.Disposition, Is.EqualTo(IngressDisposition.Accepted));
        Assert.That(result.ResponseBytes, Is.EqualTo(expected));
        mediator.Verify(m => m.Send(It.IsAny<DeliverOpaqueMessageCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
