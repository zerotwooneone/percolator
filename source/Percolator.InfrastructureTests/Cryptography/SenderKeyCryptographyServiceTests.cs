using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Cryptography;
using Percolator.Cryptography;
using System.Collections.Concurrent;

namespace Percolator.InfrastructureTests.Cryptography;

// A simple in-memory bridge for testing
public class InMemorySenderKeyInteropBridge : ISenderKeyInteropBridge
{
    private readonly ConcurrentDictionary<(ConversationId, CryptoPublicIdentity, DeviceId), byte[]> _store = new();

    public bool TryLoadSenderKey(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId deviceId, out byte[] recordBytes)
    {
        return _store.TryGetValue((conversationId, senderPublicIdentityId, deviceId), out recordBytes!);
    }

    public void StoreSenderKey(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId deviceId, byte[] recordBytes)
    {
        _store[(conversationId, senderPublicIdentityId, deviceId)] = recordBytes;
    }
}

[TestFixture]
public class SenderKeyCryptographyServiceTests
{
    [Test]
    public void SenderKey_FullRoundTrip_Succeeds()
    {
        // Arrange
        var senderBridge = new InMemorySenderKeyInteropBridge();
        var receiverBridge = new InMemorySenderKeyInteropBridge();
        
        using var senderService = new SenderKeyCryptographyService(senderBridge, NullLogger<SenderKeyCryptographyService>.Instance);
        using var receiverService = new SenderKeyCryptographyService(receiverBridge, NullLogger<SenderKeyCryptographyService>.Instance);

        var conversationId = ConversationId.NewId();
        var senderPublicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var senderDeviceId = new DeviceId(1);
        var receiverPublicIdentityId = new CryptoPublicIdentity(Guid.NewGuid());
        var receiverDeviceId = new DeviceId(1);

        // 1. Sender generates distribution message for the receiver
        var distributionMessage = senderService.CreateSenderKeyDistributionMessage(conversationId, senderPublicIdentityId, senderDeviceId);
        
        distributionMessage.Should().NotBeNull();
        distributionMessage.Span.Length.Should().BeGreaterThan(0);

        // 2. Receiver processes the distribution message to store the sender's key state
        receiverService.ProcessSenderKeyDistributionMessage(conversationId, senderPublicIdentityId, senderDeviceId, distributionMessage);

        // 3. Sender encrypts a group message
        var originalPlaintext = "Hello Group V2!"u8.ToArray();
        var ciphertext = senderService.EncryptGroupMessage(conversationId, senderPublicIdentityId, senderDeviceId, originalPlaintext);

        ciphertext.Should().NotBeNull();
        ciphertext.Length.Should().BeGreaterThan(0);
        ciphertext.Should().NotBeEquivalentTo(originalPlaintext); // Ensure it's actually encrypted

        // 4. Receiver decrypts the group message
        var decryptedPlaintext = receiverService.DecryptGroupMessage(conversationId, senderPublicIdentityId, senderDeviceId, ciphertext);

        // Assert
        decryptedPlaintext.Should().BeEquivalentTo(originalPlaintext);
    }
}
