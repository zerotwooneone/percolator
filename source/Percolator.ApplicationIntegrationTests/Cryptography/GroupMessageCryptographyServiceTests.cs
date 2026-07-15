using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Contracts;

namespace Percolator.ApplicationIntegrationTests.Cryptography;

[TestFixture]
public class GroupMessageCryptographyServiceTests
{
    private GroupMessageCryptographyService _sut;
    private FakeSenderKeyCryptographyService _fakeSenderKeyCryptoService;

    [SetUp]
    public void SetUp()
    {
        _fakeSenderKeyCryptoService = new FakeSenderKeyCryptographyService();
        _sut = new GroupMessageCryptographyService(_fakeSenderKeyCryptoService);
    }

    [Test]
    public void EncryptGroupContent_WhenCalled_SerializesContentAndDelegatesToSenderKeyService()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var deviceId = new DeviceId(1);
        var content = new GroupContent { TextMessage = "Hello, group!" };
        var expectedCiphertext = new byte[] { 1, 2, 3, 4, 5 };
        _fakeSenderKeyCryptoService.EncryptResult = expectedCiphertext;

        // Act
        var result = _sut.EncryptGroupContent(conversationId, publicIdentityId, deviceId, content);

        // Assert
        result.Span.ToArray().Should().BeEquivalentTo(expectedCiphertext);
        _fakeSenderKeyCryptoService.EncryptCallCount.Should().Be(1);
        _fakeSenderKeyCryptoService.LastEncryptConversationId.Should().Be(conversationId);
        _fakeSenderKeyCryptoService.LastEncryptPublicIdentityId.Should().Be(publicIdentityId);
        _fakeSenderKeyCryptoService.LastEncryptDeviceId.Should().Be(deviceId);
        _fakeSenderKeyCryptoService.LastEncryptPlaintext.Should().NotBeNull();
        _fakeSenderKeyCryptoService.LastEncryptPlaintext.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public void DecryptGroupContent_WhenCalled_DelegatesToSenderKeyServiceAndDeserializesContent()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var senderPublicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var senderDeviceId = new DeviceId(1);
        var originalContent = new GroupContent { TextMessage = "Hello, group!" };
        var contentBytes = Google.Protobuf.MessageExtensions.ToByteArray(originalContent);
        var ciphertext = Ciphertext.FromBytes(new byte[] { 1, 2, 3, 4, 5 });
        _fakeSenderKeyCryptoService.DecryptResult = contentBytes;

        // Act
        var result = _sut.DecryptGroupContent(conversationId, senderPublicIdentityId, senderDeviceId, ciphertext);

        // Assert
        result.TextMessage.Should().Be(originalContent.TextMessage);
        _fakeSenderKeyCryptoService.DecryptCallCount.Should().Be(1);
        _fakeSenderKeyCryptoService.LastDecryptConversationId.Should().Be(conversationId);
        _fakeSenderKeyCryptoService.LastDecryptPublicIdentityId.Should().Be(senderPublicIdentityId);
        _fakeSenderKeyCryptoService.LastDecryptDeviceId.Should().Be(senderDeviceId);
    }

    [Test]
    public void EncryptAndDecryptGroupContent_WhenValid_RoundTripsSuccessfully()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var deviceId = new DeviceId(1);
        var originalContent = new GroupContent { TextMessage = "Hello, group! This is a test message." };

        byte[]? capturedPlaintext = null;
        _fakeSenderKeyCryptoService.EncryptResult = new byte[] { 1, 2, 3, 4, 5 }; // Non-empty array to pass Ciphertext validation
        _fakeSenderKeyCryptoService.EncryptCallback = (plaintext) => capturedPlaintext = plaintext.ToArray();
        _fakeSenderKeyCryptoService.DecryptCallback = () => capturedPlaintext;

        // Act
        var ciphertext = _sut.EncryptGroupContent(conversationId, publicIdentityId, deviceId, originalContent);
        var decryptedContent = _sut.DecryptGroupContent(conversationId, publicIdentityId, deviceId, ciphertext);

        // Assert
        decryptedContent.TextMessage.Should().Be(originalContent.TextMessage);
    }

    // Manual test double since Moq cannot mock methods with ReadOnlySpan<byte> parameters
    private class FakeSenderKeyCryptographyService : ISenderKeyCryptographyService
    {
        public byte[]? EncryptResult { get; set; }
        public byte[]? DecryptResult { get; set; }
        public Action<ReadOnlySpan<byte>>? EncryptCallback { get; set; }
        public Func<byte[]>? DecryptCallback { get; set; }

        public int EncryptCallCount { get; private set; }
        public int DecryptCallCount { get; private set; }

        public ConversationId LastEncryptConversationId { get; private set; }
        public CryptoPublicIdentity LastEncryptPublicIdentityId { get; private set; }
        public DeviceId LastEncryptDeviceId { get; private set; }
        public byte[] LastEncryptPlaintext { get; private set; }

        public ConversationId LastDecryptConversationId { get; private set; }
        public CryptoPublicIdentity LastDecryptPublicIdentityId { get; private set; }
        public DeviceId LastDecryptDeviceId { get; private set; }

        public byte[] EncryptGroupMessage(ConversationId conversationId, CryptoPublicIdentity publicIdentityId, DeviceId deviceId, ReadOnlySpan<byte> plaintext)
        {
            EncryptCallCount++;
            LastEncryptConversationId = conversationId;
            LastEncryptPublicIdentityId = publicIdentityId;
            LastEncryptDeviceId = deviceId;
            LastEncryptPlaintext = plaintext.ToArray();
            EncryptCallback?.Invoke(plaintext);
            return EncryptResult ?? Array.Empty<byte>();
        }

        public byte[] DecryptGroupMessage(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId senderDeviceId, ReadOnlySpan<byte> ciphertext)
        {
            DecryptCallCount++;
            LastDecryptConversationId = conversationId;
            LastDecryptPublicIdentityId = senderPublicIdentityId;
            LastDecryptDeviceId = senderDeviceId;
            return DecryptCallback?.Invoke() ?? DecryptResult ?? Array.Empty<byte>();
        }

        public SenderKeyDistributionMessageBytes CreateSenderKeyDistributionMessage(ConversationId conversationId, CryptoPublicIdentity publicIdentityId, DeviceId deviceId)
        {
            throw new NotImplementedException();
        }

        public void ProcessSenderKeyDistributionMessage(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId senderDeviceId, SenderKeyDistributionMessageBytes distributionMessage)
        {
            throw new NotImplementedException();
        }
    }
}
