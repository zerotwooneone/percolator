using FluentAssertions;
using Moq;
using Percolator.Network;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Contracts;

namespace Percolator.NetworkTests;

[TestFixture]
public class PeerDiscoveryServiceTests
{
    private Mock<IPeerDiscoveryHandler> _mockHandler;
    private Mock<ISigningService> _mockSigningService;
    private Mock<ILogger<PeerDiscoveryService>> _mockLogger;
    private Mock<IPeerDiscoveryConfig> _mockConfig;
    private PeerDiscoveryService _service;

    // Test Primitives
    private static readonly ECDsa _testKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    // Test Value Types
    private static readonly PublicKey _testPublicKey = new(_testKey.ExportSubjectPublicKeyInfo());
    private static readonly PublicKeyHash _testPublicKeyHash = new(SHA256.HashData(_testPublicKey.Value));

    [SetUp]
    public void Setup()
    {
        _mockHandler = new Mock<IPeerDiscoveryHandler>();
        _mockSigningService = new Mock<ISigningService>();
        _mockLogger = new Mock<ILogger<PeerDiscoveryService>>();
        _mockConfig = new Mock<IPeerDiscoveryConfig>();

        // Setup mock config
        _mockConfig.SetupGet(c => c.BroadcastPort).Returns(8888);
        _mockConfig.SetupGet(c => c.ListenPort).Returns(5001);
        _mockConfig.SetupGet(c => c.BroadcastInterval).Returns(TimeSpan.FromSeconds(5));
        _mockConfig.SetupGet(c => c.PeerExpiration).Returns(TimeSpan.FromSeconds(30));

        // Setup mock signing service with value types
        _mockSigningService.Setup(s => s.GetActivePublicKey()).Returns(_testPublicKey);
        _mockSigningService.Setup(s => s.GetActivePublicKeyHash()).Returns(_testPublicKeyHash);
        _mockSigningService.Setup(s => s.GetHash(It.IsAny<PublicKey>()))
            .Returns<PublicKey>(pk => new PublicKeyHash(SHA256.HashData(pk.Value)));

        // The constructor will be updated to match this
        _service = new PeerDiscoveryService(
            _mockConfig.Object,
            _mockHandler.Object,
            _mockSigningService.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    { 
        _testKey.Dispose();
    }

    [Test]
    public void Service_CanBeConstructedAndDisposed()
    {
        // Verifies that the service can be instantiated with the new dependencies
        _service.Should().NotBeNull();
    }

    [Test]
    public void DiscoveryBroadcast_Serialization_WorksCorrectly()
    {
        // Arrange: Create a payload, now including the public key
        var protoPayload = new DiscoveryPayload
        {
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Port = 5001,
            PublicKey = ByteString.CopyFrom(_testPublicKey.Value)
        };
        var payload = new Payload(protoPayload.ToByteArray());

        // Arrange: Create a signature and a broadcast message
        var signature = new Signature(_testKey.SignData(payload.Value, HashAlgorithmName.SHA256));
        var broadcast = new DiscoveryBroadcast
        {
            PublicKey = ByteString.CopyFrom(_testPublicKey.Value),
            Signature = ByteString.CopyFrom(signature.Value),
            Payload = ByteString.CopyFrom(payload.Value)
        };

        // Act: Serialize and deserialize the broadcast message
        var serializedBroadcast = broadcast.ToByteArray();
        var deserializedBroadcast = DiscoveryBroadcast.Parser.ParseFrom(serializedBroadcast);
        var deserializedPayload = DiscoveryPayload.Parser.ParseFrom(deserializedBroadcast.Payload);

        // Assert: Verify the contents are intact
        deserializedBroadcast.PublicKey.ToByteArray().Should().BeEquivalentTo(_testPublicKey.Value);
        deserializedBroadcast.Signature.ToByteArray().Should().BeEquivalentTo(signature.Value);
        deserializedPayload.Port.Should().Be(protoPayload.Port);
        deserializedPayload.Timestamp.Should().Be(protoPayload.Timestamp);
        deserializedPayload.PublicKey.ToByteArray().Should().BeEquivalentTo(_testPublicKey.Value);
    }
}
