using Grpc.Core;
using Moq;
using Percolator.Application.Network.RelayHost;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Network.RelayHost;

[TestFixture]
public class RelayHostStreamManagerTests
{
    private RelayHostStreamManager _manager;

    [SetUp]
    public void Setup()
    {
        _manager = new RelayHostStreamManager();
    }

    [Test]
    public void RegisterClient_AddsStreamToDictionary()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());
        var mockStream = new Mock<IServerStreamWriter<ServerRelayStream>>();

        // ACT
        _manager.RegisterClient(clientId, mockStream.Object);

        // ASSERT
        Assert.That(_manager.IsClientConnected(clientId), Is.True);
    }

    [Test]
    public void RegisterClient_UpdatesExistingStream()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());
        var mockStream1 = new Mock<IServerStreamWriter<ServerRelayStream>>();
        var mockStream2 = new Mock<IServerStreamWriter<ServerRelayStream>>();

        // ACT
        _manager.RegisterClient(clientId, mockStream1.Object);
        _manager.RegisterClient(clientId, mockStream2.Object);

        // ASSERT
        Assert.That(_manager.IsClientConnected(clientId), Is.True);
        _manager.TryGetStream(clientId, out var stream);
        Assert.That(stream, Is.EqualTo(mockStream2.Object));
    }

    [Test]
    public void RemoveClient_RemovesStreamFromDictionary()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());
        var mockStream = new Mock<IServerStreamWriter<ServerRelayStream>>();
        _manager.RegisterClient(clientId, mockStream.Object);

        // ACT
        _manager.RemoveClient(clientId);

        // ASSERT
        Assert.That(_manager.IsClientConnected(clientId), Is.False);
    }

    [Test]
    public void TryGetStream_ReturnsStreamWhenConnected()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());
        var mockStream = new Mock<IServerStreamWriter<ServerRelayStream>>();
        _manager.RegisterClient(clientId, mockStream.Object);

        // ACT
        var result = _manager.TryGetStream(clientId, out var stream);

        // ASSERT
        Assert.That(result, Is.True);
        Assert.That(stream, Is.EqualTo(mockStream.Object));
    }

    [Test]
    public void TryGetStream_ReturnsFalseWhenNotConnected()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());

        // ACT
        var result = _manager.TryGetStream(clientId, out var stream);

        // ASSERT
        Assert.That(result, Is.False);
        Assert.That(stream, Is.Null);
    }

    [Test]
    public void IsClientConnected_ReturnsTrueWhenConnected()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());
        var mockStream = new Mock<IServerStreamWriter<ServerRelayStream>>();
        _manager.RegisterClient(clientId, mockStream.Object);

        // ACT
        var result = _manager.IsClientConnected(clientId);

        // ASSERT
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsClientConnected_ReturnsFalseWhenNotConnected()
    {
        // ARRANGE
        var clientId = new PublicIdentityId(Guid.NewGuid());

        // ACT
        var result = _manager.IsClientConnected(clientId);

        // ASSERT
        Assert.That(result, Is.False);
    }

    [Test]
    public void MultipleClients_AllManagedIndependently()
    {
        // ARRANGE
        var client1 = new PublicIdentityId(Guid.NewGuid());
        var client2 = new PublicIdentityId(Guid.NewGuid());
        var mockStream1 = new Mock<IServerStreamWriter<ServerRelayStream>>();
        var mockStream2 = new Mock<IServerStreamWriter<ServerRelayStream>>();

        // ACT
        _manager.RegisterClient(client1, mockStream1.Object);
        _manager.RegisterClient(client2, mockStream2.Object);

        // ASSERT
        Assert.That(_manager.IsClientConnected(client1), Is.True);
        Assert.That(_manager.IsClientConnected(client2), Is.True);
        _manager.TryGetStream(client1, out var stream1);
        _manager.TryGetStream(client2, out var stream2);
        Assert.That(stream1, Is.EqualTo(mockStream1.Object));
        Assert.That(stream2, Is.EqualTo(mockStream2.Object));
    }
}
