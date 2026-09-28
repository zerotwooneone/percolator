using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public class PeerContactTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private IdentityKey _primaryPeerKey = null!;
    private IdentityKey _secondaryDeviceKey = null!;
    private DeviceLinkProof _dummyProof = null!;
    private PeerContact _contact = null!;
    private static readonly DateTimeOffset FixedCreatedAt = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _primaryPeerKey = IdentityKey.FromSpan(new byte[32]);
        _secondaryDeviceKey = IdentityKey.FromSpan(new byte[32]);
        _dummyProof = DeviceLinkProof.FromSpan(new byte[64]);

        _contact = new PeerContact(
            ownerIdentityId: PublicIdentityId.New(),
            remotePeerId: PublicIdentityId.New(),
            nickname: "Alice",
            trustLevel: PeerTrustLevel.Tofu,
            createdAtUtc: FixedCreatedAt);
    }

    [Test]
    public void RegisterSecondaryDevice_WithValidSignature_Succeeds()
    {
        var secondaryId = new DeviceId(2);
        var result = _contact.RegisterSecondaryDevice(
            secondaryId,
            _secondaryDeviceKey,
            _dummyProof,
            _primaryPeerKey,
            _cryptoEngine);

        result.IsSuccess.Should().BeTrue();
        _contact.RegisteredDevices.Should().Contain(secondaryId);
    }

    [Test]
    public void RegisterSecondaryDevice_DeviceIdOne_Fails()
    {
        var result = _contact.RegisterSecondaryDevice(
            DeviceId.Primary,
            _secondaryDeviceKey,
            _dummyProof,
            _primaryPeerKey,
            _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INVALID_DEVICE_ID");
    }

    [Test]
    public void RegisterSecondaryDevice_InvalidSignature_Fails()
    {
        _cryptoEngine.SignaturesAlwaysValid = false;

        var result = _contact.RegisterSecondaryDevice(
            new DeviceId(5),
            _secondaryDeviceKey,
            _dummyProof,
            _primaryPeerKey,
            _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INVALID_LINK_PROOF_SIGNATURE");
        _contact.RegisteredDevices.Should().NotContain(new DeviceId(5));
    }

    [Test]
    public void RegisterSecondaryDevice_AlreadyRegistered_IsIdempotent()
    {
        var secondaryId = new DeviceId(2);
        _contact.RegisterSecondaryDevice(secondaryId, _secondaryDeviceKey, _dummyProof, _primaryPeerKey, _cryptoEngine);

        var countBefore = _contact.RegisteredDevices.Count;
        var duplicateResult = _contact.RegisterSecondaryDevice(secondaryId, _secondaryDeviceKey, _dummyProof, _primaryPeerKey, _cryptoEngine);

        duplicateResult.IsSuccess.Should().BeTrue();
        _contact.RegisteredDevices.Count.Should().Be(countBefore);
    }

    [Test]
    public void RegisterSecondaryDevice_ExceedsMaxLimit_Fails()
    {
        for (uint i = 2; i <= PeerContact.MaxRegisteredDevicesPerPeer; i++)
        {
            var res = _contact.RegisterSecondaryDevice(new DeviceId(i), _secondaryDeviceKey, _dummyProof, _primaryPeerKey, _cryptoEngine);
            res.IsSuccess.Should().BeTrue();
        }

        var overflowId = new DeviceId(PeerContact.MaxRegisteredDevicesPerPeer + 1);
        var overflowResult = _contact.RegisterSecondaryDevice(overflowId, _secondaryDeviceKey, _dummyProof, _primaryPeerKey, _cryptoEngine);

        overflowResult.IsFailure.Should().BeTrue();
        overflowResult.Error!.Code.Should().Be("DEVICE_LIMIT_EXCEEDED");
    }

    [Test]
    public void UpdateTrust_ChangesTrustLevel()
    {
        _contact.TrustLevel.Should().Be(PeerTrustLevel.Tofu);
        _contact.UpdateTrust(PeerTrustLevel.Verified);
        _contact.TrustLevel.Should().Be(PeerTrustLevel.Verified);
    }
}
