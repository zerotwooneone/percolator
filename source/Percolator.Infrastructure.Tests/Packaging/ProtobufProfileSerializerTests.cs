namespace Percolator.Infrastructure.Tests.Packaging;

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Infrastructure2.Serialization;
using Percolator.PluginSdk;

[TestFixture]
public sealed class ProtobufProfileSerializerTests
{
    private readonly ProtobufProfileSerializer _serializer = new();

    [Test]
    public void ProfileMetadataAndBlobReference_RoundtripsCorrectly()
    {
        var blobRef = new BlobReference
        {
            Id = new BlobId("abcdef0123456789abcdef0123456789"),
            CiphertextSha256 = new byte[32],
            EncryptionKey = new byte[32],
            BaseNonce = new byte[12],
            PlaintextSizeBytes = 2048,
            CiphertextSizeBytes = 2064,
            MimeType = "image/png",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            StorageMode = BlobStorageMode.PublicRelayBlob,
            RelayEndpointUri = "https://relay.percolator.net/blobs",
            BlurHash = "L6Pj0^jE.AyE_3t7t7R**0o#DgR4",
            InlineThumbnail = [1, 2, 3]
        };

        var metadata = new ProfileMetadata(
            DisplayName: "Alice",
            AvatarBytes: [10, 20, 30],
            StatusBio: "Available",
            Revision: 3,
            AvatarReference: blobRef);

        var bytes = _serializer.Serialize(metadata);
        bytes.Should().NotBeEmpty();

        var result = _serializer.Deserialize(bytes);
        result.IsSuccess.Should().BeTrue();
        var unpacked = result.Value!;
        unpacked.DisplayName.Should().Be("Alice");
        unpacked.StatusBio.Should().Be("Available");
        unpacked.Revision.Should().Be(3);
        unpacked.AvatarBytes.Should().Equal([10, 20, 30]);
        unpacked.AvatarReference.Should().NotBeNull();
        unpacked.AvatarReference!.Id.HexDigest.Should().Be(blobRef.Id.HexDigest);
        unpacked.AvatarReference.MimeType.Should().Be("image/png");
    }
}
