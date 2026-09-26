using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public class PeerContactTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private IdentityPublicKey _primaryPeerKey = null!;
    private IdentityPublicKey _secondaryDeviceKey = null!;
    private DeviceLinkProof _dummyProof = null!;
    private PeerContact _contact = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _primaryPeerKey = IdentityPublicKey.FromSpan(new byte[32]);
        _secondaryDeviceKey = IdentityPublicKey.FromSpan(new byte[32]);
        _dummyProof = DeviceLinkProof.FromSpan(new byte[64]);

        _contact = new PeerContact(
            ownerIdentityId: PublicIdentityId.New(),
            remotePeerId: PublicIdentityId.New(),
            nickname: "Alice",
            trustLevel: PeerTrustLevel.Tofu,
            createdAtUtc: DateTimeOffset.UtcNow);
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
    public void RegisterSecondaryDevice_WithInvalidSignature_RejectsDeviceInjection()
    {
        _cryptoEngine.SignaturesAlwaysValid = false;
        var secondaryId = new DeviceId(2);

        var result = _contact.RegisterSecondaryDevice(
            secondaryId,
            _secondaryDeviceKey,
            _dummyProof,
            _primaryPeerKey,
            _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_LINK_PROOF_SIGNATURE");
        _contact.RegisteredDevices.Should().NotContain(secondaryId);
    }

    [Test]
    public void RegisterSecondaryDevice_WhenCapacityExceeded_ReturnsError()
    {
        for (uint i = 2; i <= PeerContact.MaxRegisteredDevicesPerPeer; i++)
        {
            _contact.RegisterSecondaryDevice(
                new DeviceId(i),
                _secondaryDeviceKey,
                _dummyProof,
                _primaryPeerKey,
                _cryptoEngine);
        }

        var result = _contact.RegisterSecondaryDevice(
            new DeviceId(100),
            _secondaryDeviceKey,
            _dummyProof,
            _primaryPeerKey,
            _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAX_DEVICES_EXCEEDED");
    }
}
