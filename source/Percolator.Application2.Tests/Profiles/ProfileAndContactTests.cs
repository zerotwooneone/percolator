using Percolator.Application2.Profiles;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.Profiles;

[TestFixture]
public sealed class ProfileAndContactTests
{
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private InMemoryPeerContactRepository _contactRepo = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private ProfileManager _profileManager = null!;
    private ContactRequestCoordinator _contactCoordinator = null!;

    private PublicIdentityId _aliceId;
    private PublicIdentityId _bobId;
    private IdentityKey _bobIdentityKey = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _contactRepo = new InMemoryPeerContactRepository();
        _timeProvider = new TestDateTimeProvider();
        _profileManager = new ProfileManager();
        _contactCoordinator = new ContactRequestCoordinator(_contactRepo, _timeProvider);

        _aliceId = PublicIdentityId.New();
        _bobId = PublicIdentityId.New();
        _bobIdentityKey = IdentityKey.FromBytes(new byte[32]);
    }

    [Test]
    public void UpdateProfile_EncryptsProfilePackage_AndDecryptsMatchingMetadata()
    {
        // Arrange
        using var profileKey = ProfileKey.Generate();
        var metadata = new ProfileMetadata(
            DisplayName: "Alice in Wonderland",
            AvatarBytes: [0x11, 0x22, 0x33, 0x44],
            StatusBio: "Exploring cyberspace",
            Revision: 1);

        // Act
        var encryptResult = _profileManager.EncryptProfile(metadata, profileKey, _cryptoEngine);

        // Assert Encrypt
        encryptResult.IsSuccess.Should().BeTrue();
        var package = encryptResult.Value!;
        package.Nonce.Should().HaveCount(12);
        package.Ciphertext.Should().NotBeEmpty();

        // Act Decrypt
        var decryptResult = _profileManager.DecryptProfile(package, profileKey, _cryptoEngine);

        // Assert Decrypt
        decryptResult.IsSuccess.Should().BeTrue();
        var decrypted = decryptResult.Value!;
        decrypted.DisplayName.Should().Be("Alice in Wonderland");
        decrypted.StatusBio.Should().Be("Exploring cyberspace");
        decrypted.Revision.Should().Be(1);
        decrypted.AvatarBytes.Should().BeEquivalentTo(new byte[] { 0x11, 0x22, 0x33, 0x44 });
    }

    [Test]
    public async Task InboundRequest_WhenReceived_CreatesPendingApprovalContact()
    {
        // Act
        var result = await _contactCoordinator.HandleInboundRequestAsync(
            _aliceId, _bobId, _bobIdentityKey, "Bob The Builder");

        // Assert
        result.IsSuccess.Should().BeTrue();
        var contact = result.Value!;
        contact.OwnerIdentityId.Should().Be(_aliceId);
        contact.RemotePeerId.Should().Be(_bobId);
        contact.Nickname.Should().Be("Bob The Builder");
        contact.State.Should().Be(ContactState.PendingApproval);
        contact.TrustLevel.Should().Be(PeerTrustLevel.Untrusted);
    }

    [Test]
    public async Task ApproveRequest_TransitionsContactToActive()
    {
        // Arrange
        await _contactCoordinator.HandleInboundRequestAsync(_aliceId, _bobId, _bobIdentityKey, "Bob");

        // Act
        var result = await _contactCoordinator.ApproveRequestAsync(_aliceId, _bobId, PeerTrustLevel.Verified);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var stored = await _contactRepo.GetByPeerIdAsync(_aliceId, _bobId);
        stored.Should().NotBeNull();
        stored!.State.Should().Be(ContactState.Active);
        stored.TrustLevel.Should().Be(PeerTrustLevel.Verified);
    }

    [Test]
    public async Task RejectRequest_TransitionsContactToRejectedAndBlocks()
    {
        // Arrange
        await _contactCoordinator.HandleInboundRequestAsync(_aliceId, _bobId, _bobIdentityKey, "Spammer");

        // Act
        var result = await _contactCoordinator.RejectRequestAsync(_aliceId, _bobId, block: true);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var stored = await _contactRepo.GetByPeerIdAsync(_aliceId, _bobId);
        stored.Should().NotBeNull();
        stored!.State.Should().Be(ContactState.Rejected);
        stored.TrustLevel.Should().Be(PeerTrustLevel.Blocked);
    }

    [Test]
    public async Task DuplicateInboundRequest_WhenContactAlreadyActive_DoesNotRegressStateOrTrust()
    {
        // Arrange: Inbound request received and approved with Verified trust
        await _contactCoordinator.HandleInboundRequestAsync(_aliceId, _bobId, _bobIdentityKey, "Bob");
        await _contactCoordinator.ApproveRequestAsync(_aliceId, _bobId, PeerTrustLevel.Verified);

        // Act: Duplicate request arrives from network with different nickname
        var duplicateResult = await _contactCoordinator.HandleInboundRequestAsync(
            _aliceId, _bobId, _bobIdentityKey, "Bob The Impostor");

        // Assert: Idempotent - returns existing active contact without regressing to PendingApproval
        duplicateResult.IsSuccess.Should().BeTrue();
        var contact = duplicateResult.Value!;
        contact.State.Should().Be(ContactState.Active);
        contact.TrustLevel.Should().Be(PeerTrustLevel.Verified);
        contact.Nickname.Should().Be("Bob");
    }
}
