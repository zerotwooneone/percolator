using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.Profiles;

[TestFixture]
public sealed class ProfileSecurityAndQueryTests
{
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private ProfileManager _profileManager = null!;
    private InMemoryPeerProfileRepository _profileRepo = null!;
    private PeerProfileQueryService _queryService = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _profileManager = new ProfileManager(new TestProfileMetadataSerializer());
        _profileRepo = new InMemoryPeerProfileRepository();
        _queryService = new PeerProfileQueryService(_profileRepo);
    }

    [Test]
    public void EncryptProfile_AlwaysOutputs1040BytePackage_RegardlessOfMetadataSize()
    {
        using var profileKey = ProfileKey.Generate();

        // 1. Tiny metadata
        var tiny = new ProfileMetadata("A", null, null, 1);
        var tinyResult = _profileManager.EncryptProfile(tiny, profileKey, _cryptoEngine);
        tinyResult.IsSuccess.Should().BeTrue();
        tinyResult.Value.Ciphertext.Length.Should().Be(1040);

        // 2. Medium metadata
        var medium = new ProfileMetadata("Alice", new byte[50], "A long bio with description", 1);
        var medResult = _profileManager.EncryptProfile(medium, profileKey, _cryptoEngine);
        medResult.IsSuccess.Should().BeTrue();
        medResult.Value.Ciphertext.Length.Should().Be(1040);
    }

    [Test]
    public void DecryptProfile_WithMismatchedRevision_FailsTamperCheck()
    {
        using var profileKey = ProfileKey.Generate();
        var metadata = new ProfileMetadata("Alice", null, "Bio", Revision: 5);

        var encryptResult = _profileManager.EncryptProfile(metadata, profileKey, _cryptoEngine);
        encryptResult.IsSuccess.Should().BeTrue();

        // Attempt decrypt with wrong expected revision (e.g. 4 or 6)
        var decryptResult = _profileManager.DecryptProfile(encryptResult.Value, profileKey, _cryptoEngine, expectedRevision: 4);
        decryptResult.IsSuccess.Should().BeFalse();
        decryptResult.Error.Code.Should().BeOneOf("PROFILE_DECRYPT_FAILED", "REVISION_MISMATCH");

        // Decrypt with correct expected revision succeeds
        var validDecrypt = _profileManager.DecryptProfile(encryptResult.Value, profileKey, _cryptoEngine, expectedRevision: 5);
        validDecrypt.IsSuccess.Should().BeTrue();
        validDecrypt.Value.Revision.Should().Be(5);
    }

    [Test]
    public void ProfileWithAvatarReference_RoundtripsSuccessfully()
    {
        using var profileKey = ProfileKey.Generate();
        var avatarRef = new BlobReference
        {
            Id = new BlobId("test-avatar-blob"),
            CiphertextSha256 = new byte[32],
            EncryptionKey = new byte[32],
            BaseNonce = new byte[12],
            PlaintextSizeBytes = 1024,
            CiphertextSizeBytes = 1040,
            MimeType = "image/png",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            StorageMode = BlobStorageMode.PublicRelayBlob
        };

        var metadata = new ProfileMetadata(
            DisplayName: "Bob",
            AvatarBytes: null,
            StatusBio: "Avatar tester",
            Revision: 2,
            AvatarReference: avatarRef);

        var encryptResult = _profileManager.EncryptProfile(metadata, profileKey, _cryptoEngine);
        encryptResult.IsSuccess.Should().BeTrue();

        var decryptResult = _profileManager.DecryptProfile(encryptResult.Value, profileKey, _cryptoEngine, expectedRevision: 2);
        decryptResult.IsSuccess.Should().BeTrue();
        var decrypted = decryptResult.Value;
        decrypted.DisplayName.Should().Be("Bob");
        decrypted.AvatarReference.Should().NotBeNull();
        decrypted.AvatarReference!.Id.Should().Be(avatarRef.Id);
        decrypted.AvatarReference.StorageMode.Should().Be(BlobStorageMode.PublicRelayBlob);
    }

    [Test]
    public async Task QueryService_RetrievesAndMapsProfilesCorrectly()
    {
        var aliceId = PublicIdentityId.New();
        var bobId = PublicIdentityId.New();

        var aliceRecord = new PeerProfileRecord(
            aliceId,
            "Alice",
            "Wonderland",
            null,
            1,
            DateTimeOffset.UtcNow);

        await _profileRepo.SaveAsync(aliceRecord);

        // Act Single
        var aliceRead = await _queryService.GetProfileAsync(aliceId);
        aliceRead.Should().NotBeNull();
        aliceRead!.DisplayName.Should().Be("Alice");
        aliceRead.HasAvatar.Should().BeFalse();

        var bobRead = await _queryService.GetProfileAsync(bobId);
        bobRead.Should().BeNull();

        // Act Batch
        var batch = await _queryService.GetProfilesAsync(new[] { aliceId, bobId });
        batch.Should().ContainKey(aliceId);
        batch.Should().NotContainKey(bobId);
    }
}
