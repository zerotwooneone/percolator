using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Infrastructure2.Serialization;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Infrastructure;

[TestFixture]
public sealed class ProtobufProfileSerializerTests
{
    private ProtobufProfileSerializer _serializer = null!;

    [SetUp]
    public void SetUp()
    {
        _serializer = new ProtobufProfileSerializer();
    }

    [Test]
    public void Serialize_And_Deserialize_BasicMetadata_RoundTripsAccurately()
    {
        // Arrange
        var original = new ProfileMetadata(
            DisplayName: "Alice in Wonderland",
            AvatarBytes: [1, 2, 3, 4, 5],
            StatusBio: "Exploring the rabbit hole",
            Revision: 42,
            AvatarReference: null);

        // Act
        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize(bytes);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var deserialized = result.Value!;
        deserialized.DisplayName.Should().Be("Alice in Wonderland");
        deserialized.StatusBio.Should().Be("Exploring the rabbit hole");
        deserialized.Revision.Should().Be(42u);
        deserialized.AvatarBytes.Should().Equal([1, 2, 3, 4, 5]);
        deserialized.AvatarReference.Should().BeNull();
    }

    [Test]
    public void Serialize_And_Deserialize_WithBlobReference_RoundTripsAccurately()
    {
        // Arrange
        var blobRef = new BlobReference
        {
            Id = new BlobId("abcdef0123456789abcdef0123456789"),
            CiphertextSha256 = new byte[32],
            EncryptionKey = new byte[32],
            BaseNonce = new byte[12],
            PlaintextSizeBytes = 10240,
            CiphertextSizeBytes = 10256,
            MimeType = "image/png",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(14),
            StorageMode = BlobStorageMode.PublicRelayBlob,
            RelayEndpointUri = "https://relay.percolator.net/blobs",
            BlurHash = "L6Pj0^jE.AyE_3t7t7R**0o#DgR4",
            InlineThumbnail = [10, 20, 30],
            Waveform = null,
            Width = 512,
            Height = 512,
            DurationSeconds = null
        };

        var original = new ProfileMetadata(
            DisplayName: "Bob Builder",
            AvatarBytes: null,
            StatusBio: "Can we fix it?",
            Revision: 3,
            AvatarReference: blobRef);

        // Act
        var bytes = _serializer.Serialize(original);
        var result = _serializer.Deserialize(bytes);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var deserialized = result.Value!;
        deserialized.DisplayName.Should().Be("Bob Builder");
        deserialized.StatusBio.Should().Be("Can we fix it?");
        deserialized.Revision.Should().Be(3u);
        deserialized.AvatarBytes.Should().BeNull();
        deserialized.AvatarReference.Should().NotBeNull();
        deserialized.AvatarReference!.Id.HexDigest.Should().Be(blobRef.Id.HexDigest);
        deserialized.AvatarReference.MimeType.Should().Be("image/png");
        deserialized.AvatarReference.PlaintextSizeBytes.Should().Be(10240);
        deserialized.AvatarReference.CiphertextSizeBytes.Should().Be(10256);
        deserialized.AvatarReference.StorageMode.Should().Be(BlobStorageMode.PublicRelayBlob);
        deserialized.AvatarReference.RelayEndpointUri.Should().Be("https://relay.percolator.net/blobs");
        deserialized.AvatarReference.BlurHash.Should().Be("L6Pj0^jE.AyE_3t7t7R**0o#DgR4");
        deserialized.AvatarReference.InlineThumbnail.Should().Equal([10, 20, 30]);
        deserialized.AvatarReference.Width.Should().Be(512);
        deserialized.AvatarReference.Height.Should().Be(512);
    }

    [Test]
    public void Deserialize_EmptySpan_ReturnsFailure()
    {
        // Act
        var result = _serializer.Deserialize(ReadOnlySpan<byte>.Empty);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMPTY_DATA");
    }

    [Test]
    public void Serialization_Size_IsCompact_WellUnder1024Bytes()
    {
        // Arrange
        var original = new ProfileMetadata(
            DisplayName: "A Very Long Display Name For Testing Protobuf Size",
            AvatarBytes: new byte[256],
            StatusBio: "A detailed status bio with some rich description to measure payload compactness.",
            Revision: 100);

        // Act
        var bytes = _serializer.Serialize(original);

        // Assert: Protobuf overhead is tiny (~10-15 bytes), well under the 1024-byte ISO/IEC padding ceiling
        bytes.Length.Should().BeLessThan(400);
    }
}
