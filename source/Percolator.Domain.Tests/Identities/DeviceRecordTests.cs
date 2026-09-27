using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public class DeviceRecordTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private IdentityPublicKey _dummyKey = null!;
    private IdentityPublicKey _primaryKey = null!;
    private DeviceLinkProof _dummyProof = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        _cryptoEngine = new DeterministicCryptoEngine();
        _dummyKey = IdentityPublicKey.FromSpan(new byte[32]);
        _primaryKey = IdentityPublicKey.FromSpan(new byte[32]);
        _dummyProof = DeviceLinkProof.FromSpan(new byte[64]);
    }

    [Test]
    public void CreatePrimaryDevice_HasDeviceIdOne_AndRequiresNoProof()
    {
        var result = DeviceRecord.CreatePrimary(_dummyKey, "Primary Laptop", _timeProvider);

        result.IsSuccess.Should().BeTrue();
        var device = result.Value;
        device.Id.Should().Be(DeviceId.Primary);
        device.Id.IsPrimary.Should().BeTrue();
        device.DeviceName.Should().Be("Primary Laptop");
        device.LinkProof.Should().BeNull();
        device.CreatedAtUtc.Should().Be(_timeProvider.UtcNow);
    }

    [Test]
    public void CreateSecondaryDevice_WithLinkProof_Succeeds()
    {
        var secondaryId = new DeviceId(2);
        var result = DeviceRecord.CreateSecondary(
            secondaryId,
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Work Phone",
            _timeProvider);

        result.IsSuccess.Should().BeTrue();
        var device = result.Value;
        device.Id.Should().Be(secondaryId);
        device.Id.IsPrimary.Should().BeFalse();
        device.LinkProof.Should().NotBeNull();
        device.LinkProof.Should().Be(_dummyProof);
    }

    [Test]
    public void CreateSecondaryDevice_WithInvalidSignature_ReturnsError()
    {
        _cryptoEngine.SignaturesAlwaysValid = false;
        var secondaryId = new DeviceId(2);
        var result = DeviceRecord.CreateSecondary(
            secondaryId,
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Work Phone",
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_LINK_PROOF_SIGNATURE");
    }

    [Test]
    public void CreateSecondaryDevice_WithPrimaryId_ReturnsError()
    {
        var result = DeviceRecord.CreateSecondary(
            DeviceId.Primary,
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Phone",
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_DEVICE_ID");
    }

    [Test]
    public void CreateSecondaryDevice_WithZeroDeviceId_ReturnsError()
    {
        var result = DeviceRecord.CreateSecondary(
            new DeviceId(0),
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Phone",
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_DEVICE_ID");
    }

    [Test]
    public void CreateSecondaryDevice_WithoutLinkProof_ReturnsError()
    {
        var result = DeviceRecord.CreateSecondary(
            new DeviceId(2),
            _dummyKey,
            null!,
            _primaryKey,
            _cryptoEngine,
            "Phone",
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MISSING_LINK_PROOF");
    }
}
