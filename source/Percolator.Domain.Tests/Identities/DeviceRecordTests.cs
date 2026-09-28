using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public class DeviceRecordTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private IdentityKey _dummyKey = null!;
    private IdentityKey _primaryKey = null!;
    private DeviceLinkProof _dummyProof = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        _cryptoEngine = new DeterministicCryptoEngine();
        _dummyKey = IdentityKey.FromSpan(new byte[32]);
        _primaryKey = IdentityKey.FromSpan(new byte[32]);
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
        device.DevicePublicKey.Should().Be(_dummyKey);
        device.LinkProof.Should().BeNull();
        device.CreatedAtUtc.Should().Be(_timeProvider.UtcNow);
        device.LastSeenAtUtc.Should().BeNull();
    }

    [Test]
    public void CreatePrimaryDevice_NullPublicKey_Fails()
    {
        var result = DeviceRecord.CreatePrimary(null!, "Primary", _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NULL_PUBLIC_KEY");
    }

    [Test]
    public void CreateSecondaryDevice_WithValidLinkProof_Succeeds()
    {
        var secondaryId = new DeviceId(2);
        var result = DeviceRecord.CreateSecondary(
            secondaryId,
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Phone",
            _timeProvider);

        result.IsSuccess.Should().BeTrue();
        var device = result.Value;
        device.Id.Should().Be(secondaryId);
        device.Id.IsPrimary.Should().BeFalse();
        device.DeviceName.Should().Be("Phone");
        device.LinkProof.Should().Be(_dummyProof);
    }

    [Test]
    public void CreateSecondaryDevice_WithDeviceIdOne_Fails()
    {
        var result = DeviceRecord.CreateSecondary(
            DeviceId.Primary,
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Fake Secondary",
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INVALID_DEVICE_ID");
    }

    [Test]
    public void CreateSecondaryDevice_WithInvalidSignature_Fails()
    {
        _cryptoEngine.SignaturesAlwaysValid = false;

        var result = DeviceRecord.CreateSecondary(
            new DeviceId(3),
            _dummyKey,
            _dummyProof,
            _primaryKey,
            _cryptoEngine,
            "Compromised Phone",
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("INVALID_LINK_PROOF_SIGNATURE");
    }

    [Test]
    public void TouchLastSeen_UpdatesTimestamp()
    {
        var device = DeviceRecord.CreatePrimary(_dummyKey, "Laptop", _timeProvider).Value;
        device.LastSeenAtUtc.Should().BeNull();

        _timeProvider.Advance(TimeSpan.FromMinutes(15));
        device.TouchLastSeen(_timeProvider);

        device.LastSeenAtUtc.Should().Be(_timeProvider.UtcNow);
    }
}
