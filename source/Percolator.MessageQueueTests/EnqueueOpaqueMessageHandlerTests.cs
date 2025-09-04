using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.MessageQueue.Abstractions;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Handlers;

namespace Percolator.MessageQueueTests;

[TestFixture]
public class EnqueueOpaqueMessageHandlerTests
{
    private Mock<ILogger<EnqueueOpaqueMessageHandler>> _logger = null!;
    private Mock<IPeerPublicSigningKeyStore> _keyStore = null!;
    private Mock<IMessageQueueRepository> _repo = null!;
    private EnqueueOpaqueMessageHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new Mock<ILogger<EnqueueOpaqueMessageHandler>>();
        _keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        _repo = new Mock<IMessageQueueRepository>(MockBehavior.Strict);
        _sut = new EnqueueOpaqueMessageHandler(_logger.Object, _keyStore.Object, _repo.Object);
    }

    [Test]
    public async Task Returns_error_when_recipient_pkh_is_null()
    {
        var cmd = new EnqueueOpaqueMessageCommand(null!, new byte[] { 0x01 });
        var result = await _sut.Handle(cmd, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("recipient_public_key_hash is required");
        _keyStore.VerifyNoOtherCalls();
        _repo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_error_when_recipient_pkh_is_empty()
    {
        var cmd = new EnqueueOpaqueMessageCommand(Array.Empty<byte>(), new byte[] { 0x01 });
        var result = await _sut.Handle(cmd, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("recipient_public_key_hash is required");
        _keyStore.VerifyNoOtherCalls();
        _repo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_error_when_message_blob_is_null()
    {
        var cmd = new EnqueueOpaqueMessageCommand(new byte[32], null!);
        var result = await _sut.Handle(cmd, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("message_blob is required");
        _keyStore.VerifyNoOtherCalls();
        _repo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_error_when_message_blob_is_empty()
    {
        var cmd = new EnqueueOpaqueMessageCommand(new byte[32], Array.Empty<byte>());
        var result = await _sut.Handle(cmd, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("message_blob is required");
        _keyStore.VerifyNoOtherCalls();
        _repo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_error_when_message_blob_exceeds_limit()
    {
        var tooBig = new byte[EnqueueOpaqueMessageHandler.MaxBlobBytes + 1];
        var cmd = new EnqueueOpaqueMessageCommand(new byte[32], tooBig);
        var result = await _sut.Handle(cmd, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be($"message_blob exceeds {EnqueueOpaqueMessageHandler.MaxBlobBytes} bytes");
        _keyStore.VerifyNoOtherCalls();
        _repo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_error_when_unknown_recipient_pkh()
    {
        var pkh = new byte[32];
        var blob = new byte[] { 0xAA };
        _keyStore.Setup(k => k.GetPeerIdByPublicKeyHashAsync(pkh, It.IsAny<CancellationToken>()))
                 .ReturnsAsync((PeerId?)null);

        var cmd = new EnqueueOpaqueMessageCommand(pkh, blob);
        var result = await _sut.Handle(cmd, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("unknown recipient_public_key_hash");
        _keyStore.VerifyAll();
        _repo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Returns_error_when_repository_rejects_enqueue()
    {
        var pkh = new byte[32];
        var blob = new byte[] { 0xBB };
        var peerId = PeerId.NewId();
        _keyStore.Setup(k => k.GetPeerIdByPublicKeyHashAsync(pkh, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(peerId);
        _repo.Setup(r => r.TryEnqueueAsync(peerId, blob, It.IsAny<CancellationToken>()))
             .ReturnsAsync((false, 10u, 100u));

        var cmd = new EnqueueOpaqueMessageCommand(pkh, blob);
        var result = await _sut.Handle(cmd, CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("queue limits exceeded or rejected by policy");
        _keyStore.VerifyAll();
        _repo.VerifyAll();
    }

    [Test]
    public async Task Returns_success_when_repository_accepts_enqueue()
    {
        var pkh = new byte[32];
        var blob = new byte[] { 0xCC };
        var peerId = PeerId.NewId();
        _keyStore.Setup(k => k.GetPeerIdByPublicKeyHashAsync(pkh, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(peerId);
        _repo.Setup(r => r.TryEnqueueAsync(peerId, blob, It.IsAny<CancellationToken>()))
             .ReturnsAsync((true, 11u, 101u));

        var cmd = new EnqueueOpaqueMessageCommand(pkh, blob);
        var result = await _sut.Handle(cmd, CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.Error.Should().BeNull();
        _keyStore.VerifyAll();
        _repo.VerifyAll();
    }
}
