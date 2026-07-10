using FluentAssertions;
using System.Threading.Channels;
using Percolator.Infrastructure.Network.Grpc;

namespace Percolator.InfrastructureTests.Network.Grpc;

[TestFixture]
public class GrpcRelayGroupStreamDispatcherTests
{
    [Test]
    public void RegisterStream_CreatesNewChannel_WhenFirstStreamForConversation()
    {
        // ARRANGE
        var dispatcher = new GrpcRelayGroupStreamDispatcher();
        var conversationId = Guid.NewGuid();
        var publicIdentityId = Guid.NewGuid();

        // ACT
        var reader = dispatcher.RegisterStream(conversationId, publicIdentityId);

        // ASSERT
        reader.Should().NotBeNull();
    }

    [Test]
    public void RegisterStream_AddsToExistingDictionary_WhenConversationAlreadyHasStreams()
    {
        // ARRANGE
        var dispatcher = new GrpcRelayGroupStreamDispatcher();
        var conversationId = Guid.NewGuid();
        var firstIdentityId = Guid.NewGuid();
        var secondIdentityId = Guid.NewGuid();

        // ACT
        var firstReader = dispatcher.RegisterStream(conversationId, firstIdentityId);
        var secondReader = dispatcher.RegisterStream(conversationId, secondIdentityId);

        // ASSERT
        firstReader.Should().NotBeNull();
        secondReader.Should().NotBeNull();
        firstReader.Should().NotBeSameAs(secondReader);
    }

    [Test]
    public void UnregisterStream_RemovesChannel_WhenStreamExists()
    {
        // ARRANGE
        var dispatcher = new GrpcRelayGroupStreamDispatcher();
        var conversationId = Guid.NewGuid();
        var publicIdentityId = Guid.NewGuid();
        dispatcher.RegisterStream(conversationId, publicIdentityId);

        // ACT
        dispatcher.UnregisterStream(conversationId, publicIdentityId);

        // ASSERT - No exception thrown
    }

    [Test]
    public void UnregisterStream_CleansUpEmptyConversation_WhenLastStreamRemoved()
    {
        // ARRANGE
        var dispatcher = new GrpcRelayGroupStreamDispatcher();
        var conversationId = Guid.NewGuid();
        var publicIdentityId = Guid.NewGuid();
        dispatcher.RegisterStream(conversationId, publicIdentityId);

        // ACT
        dispatcher.UnregisterStream(conversationId, publicIdentityId);

        // ASSERT - Re-registering should create a new channel (conversation was cleaned up)
        var newReader = dispatcher.RegisterStream(conversationId, publicIdentityId);
        newReader.Should().NotBeNull();
    }

    [Test]
    public async Task DispatchAsync_WritesToAllChannels_WhenConversationHasMultipleActiveStreams()
    {
        // ARRANGE
        var dispatcher = new GrpcRelayGroupStreamDispatcher();
        var conversationId = Guid.NewGuid();
        var firstIdentityId = Guid.NewGuid();
        var secondIdentityId = Guid.NewGuid();

        var firstReader = dispatcher.RegisterStream(conversationId, firstIdentityId);
        var secondReader = dispatcher.RegisterStream(conversationId, secondIdentityId);

        var ciphertext = new byte[] { 0x01, 0x02, 0x03 };
        var epoch = 1u;
        var senderPublicIdentityId = Guid.NewGuid();

        // ACT
        await dispatcher.DispatchAsync(conversationId, ciphertext, epoch, senderPublicIdentityId, CancellationToken.None);

        // ASSERT
        var firstMessage = await firstReader.ReadAsync(CancellationToken.None);
        var secondMessage = await secondReader.ReadAsync(CancellationToken.None);

        firstMessage.Ciphertext.ToByteArray().Should().BeEquivalentTo(ciphertext);
        firstMessage.Epoch.Should().Be(epoch);
        firstMessage.SenderPublicIdentityId.ToByteArray().Should().BeEquivalentTo(senderPublicIdentityId.ToByteArray());

        secondMessage.Ciphertext.ToByteArray().Should().BeEquivalentTo(ciphertext);
        secondMessage.Epoch.Should().Be(epoch);
        secondMessage.SenderPublicIdentityId.ToByteArray().Should().BeEquivalentTo(senderPublicIdentityId.ToByteArray());
    }

    [Test]
    public async Task DispatchAsync_DoesNotThrow_WhenNoStreamsRegistered()
    {
        // ARRANGE
        var dispatcher = new GrpcRelayGroupStreamDispatcher();
        var conversationId = Guid.NewGuid();
        var ciphertext = new byte[] { 0x01, 0x02, 0x03 };
        var epoch = 1u;
        var senderPublicIdentityId = Guid.NewGuid();

        // ACT
        var act = async () => await dispatcher.DispatchAsync(conversationId, ciphertext, epoch, senderPublicIdentityId, CancellationToken.None);

        // ASSERT
        await act.Should().NotThrowAsync();
    }
}
