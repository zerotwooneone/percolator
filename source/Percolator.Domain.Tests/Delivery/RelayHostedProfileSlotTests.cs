using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public sealed class RelayHostedProfileSlotTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private FakeDateTimeProvider _dateTimeProvider = null!;
    private IdentityKey _ownerIdentityKey = null!;
    private EphemeralPrivateKey _ownerPrivateKey = null!;
    private ProfileKey _profileKey = null!;
    private BlindedProfileLocator _locator = null!;
    private ProfileWriteToken _writeToken = null!;
    private ProfileCiphertextPackage _validPackage;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _dateTimeProvider = new FakeDateTimeProvider(DateTimeOffset.UtcNow);
        _ownerIdentityKey = IdentityKey.FromSpan(new byte[32]);
        _ownerPrivateKey = EphemeralPrivateKey.FromSpan(new byte[32]);
        _profileKey = ProfileKey.Generate();
        _locator = BlindedProfileLocator.Derive(_ownerIdentityKey, _profileKey, _cryptoEngine);
        _writeToken = ProfileWriteToken.Derive(_ownerPrivateKey, _profileKey, _cryptoEngine);
        _validPackage = new ProfileCiphertextPackage(new byte[12], new byte[1040]);
    }

    [TearDown]
    public void TearDown()
    {
        _profileKey.Dispose();
        _ownerPrivateKey.Dispose();
    }

    [Test]
    public void Create_WithValidData_Succeeds()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);
        var result = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 1, expiry);

        Assert.That(result.IsSuccess, Is.True);
        var slot = result.Value;
        Assert.That(slot.Id, Is.EqualTo(_locator));
        Assert.That(slot.VerifyWriteToken(_writeToken), Is.True);
        Assert.That(slot.Revision, Is.EqualTo(1u));
        Assert.That(slot.ExpiresAtUtc, Is.EqualTo(expiry));
    }

    [Test]
    public void Create_WithNon1040BytePackageSize_ReturnsPackageSizeInvalid()
    {
        var nonUniformPackage = new ProfileCiphertextPackage(new byte[12], new byte[1041]);
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);

        var result = RelayHostedProfileSlot.Create(_locator, _writeToken, nonUniformPackage, revision: 1, expiry);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error.Code, Is.EqualTo("PACKAGE_SIZE_INVALID"));
    }

    [Test]
    public void Update_WithValidWriteTokenAndHigherRevision_Succeeds()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);
        var slot = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 1, expiry).Value;

        var newPackage = new ProfileCiphertextPackage(new byte[12], new byte[1040]);
        var newExpiry = _dateTimeProvider.UtcNow.AddDays(14);
        var updateResult = slot.Update(_writeToken, newPackage, newRevision: 2, newExpiry);

        Assert.That(updateResult.IsSuccess, Is.True);
        Assert.That(slot.Revision, Is.EqualTo(2u));
        Assert.That(slot.Package, Is.EqualTo(newPackage));
        Assert.That(slot.ExpiresAtUtc, Is.EqualTo(newExpiry));
    }

    [Test]
    public void Update_WithInvalidWriteToken_ReturnsUnauthorized()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);
        var slot = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 1, expiry).Value;

        using var differentKey = ProfileKey.Generate();
        var invalidWriteToken = ProfileWriteToken.Derive(_ownerPrivateKey, differentKey, _cryptoEngine);
        var newPackage = new ProfileCiphertextPackage(new byte[12], new byte[1040]);

        var updateResult = slot.Update(invalidWriteToken, newPackage, newRevision: 2, expiry);

        Assert.That(updateResult.IsSuccess, Is.False);
        Assert.That(updateResult.Error.Code, Is.EqualTo("UNAUTHORIZED_PROFILE_WRITE"));
    }

    [Test]
    public void Update_WithStaleOrEqualRevision_ReturnsStaleProfileRevision()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);
        var slot = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 5, expiry).Value;
        var newPackage = new ProfileCiphertextPackage(new byte[12], new byte[1040]);

        var updateEqual = slot.Update(_writeToken, newPackage, newRevision: 5, expiry);
        Assert.That(updateEqual.IsSuccess, Is.False);
        Assert.That(updateEqual.Error.Code, Is.EqualTo("STALE_PROFILE_REVISION"));

        var updateLower = slot.Update(_writeToken, newPackage, newRevision: 4, expiry);
        Assert.That(updateLower.IsSuccess, Is.False);
        Assert.That(updateLower.Error.Code, Is.EqualTo("STALE_PROFILE_REVISION"));
    }

    [Test]
    public void Update_WithNon1040BytePackageSize_ReturnsPackageSizeInvalid()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);
        var slot = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 1, expiry).Value;
        var nonUniformPackage = new ProfileCiphertextPackage(new byte[12], new byte[512]);

        var result = slot.Update(_writeToken, nonUniformPackage, newRevision: 2, expiry);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error.Code, Is.EqualTo("PACKAGE_SIZE_INVALID"));
    }

    [Test]
    public void TryGet_WhenActive_ReturnsPackage()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(7);
        var slot = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 1, expiry).Value;

        var getResult = slot.TryGet(_dateTimeProvider);
        Assert.That(getResult.IsSuccess, Is.True);
        Assert.That(getResult.Value, Is.EqualTo(_validPackage));
    }

    [Test]
    public void TryGet_WhenExpired_ReturnsProfileExpired()
    {
        var expiry = _dateTimeProvider.UtcNow.AddDays(1);
        var slot = RelayHostedProfileSlot.Create(_locator, _writeToken, _validPackage, revision: 1, expiry).Value;

        // Advance time past expiry
        _dateTimeProvider.Advance(TimeSpan.FromDays(2));

        var getResult = slot.TryGet(_dateTimeProvider);
        Assert.That(getResult.IsSuccess, Is.False);
        Assert.That(getResult.Error.Code, Is.EqualTo("PROFILE_EXPIRED"));
    }
}
