using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Chat.MessageQueue;

[TestFixture]
public class MessageQueueServiceTests
{
    private Mock<ILogger<MessageQueueService>> _logger = null!;
    private Mock<IPeerPublicSigningKeyStore> _keyStore = null!;
    private Mock<IMessageQueueRepository> _repo = null!;
    private MessageQueueService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new Mock<ILogger<MessageQueueService>>();
        _keyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Loose);
        _repo = new Mock<IMessageQueueRepository>(MockBehavior.Loose);
        _sut = new MessageQueueService(_logger.Object, _keyStore.Object, _repo.Object);
    }

    [Test]
    public async Task Returns_error_when_recipient_pkh_is_null()
    {
        var result = await _sut.EnqueueOpaqueAsync(null!, new byte[] { 0x01 }, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("recipient_public_key_hash is required");
    }

    [Test]
    public async Task Returns_error_when_recipient_pkh_is_empty()
    {
        var result = await _sut.EnqueueOpaqueAsync(Array.Empty<byte>(), new byte[] { 0x01 }, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("recipient_public_key_hash is required");
    }

    [Test]
    public async Task Returns_error_when_message_blob_is_null()
    {
        var result = await _sut.EnqueueOpaqueAsync(new byte[32], null!, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("message_blob is required");
    }

    [Test]
    public async Task Returns_error_when_message_blob_is_empty()
    {
        var result = await _sut.EnqueueOpaqueAsync(new byte[32], Array.Empty<byte>(), CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be("message_blob is required");
    }

    [Test]
    public async Task Returns_error_when_message_blob_exceeds_limit()
    {
        var tooBig = new byte[MessageQueueService.MaxBlobBytes + 1];
        var result = await _sut.EnqueueOpaqueAsync(new byte[32], tooBig, CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Error.Should().Be($"message_blob exceeds {MessageQueueService.MaxBlobBytes} bytes");
    }
}
