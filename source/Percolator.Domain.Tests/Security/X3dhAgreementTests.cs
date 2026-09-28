using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public sealed class X3dhAgreementTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
    }

    [Test]
    public void X3dh_WhenBothPartiesCompleteAgreementWithOneTimePreKey_DeriveIdenticalMasterSecret()
    {
        // Arrange
        // Alice (Initiator)
        Span<byte> aliceIdentityPriv = stackalloc byte[32];
        aliceIdentityPriv.Fill(10);
        Span<byte> aliceIdentityPub = stackalloc byte[32];
        aliceIdentityPub.Fill(110);
        var aliceIdPub = IdentityKey.FromSpan(aliceIdentityPub);

        // Bob (Receiver)
        Span<byte> bobIdentityPriv = stackalloc byte[32];
        bobIdentityPriv.Fill(20);
        Span<byte> bobIdentityPub = stackalloc byte[32];
        bobIdentityPub.Fill(120);
        var bobIdPub = IdentityKey.FromSpan(bobIdentityPub);

        Span<byte> bobSignedPreKeyPriv = stackalloc byte[32];
        bobSignedPreKeyPriv.Fill(30);
        Span<byte> bobSignedPreKeyPub = stackalloc byte[32];
        bobSignedPreKeyPub.Fill(130);
        var bobSpkPub = DhPublicKey.FromSpan(bobSignedPreKeyPub);

        Span<byte> bobOneTimePreKeyPriv = stackalloc byte[32];
        bobOneTimePreKeyPriv.Fill(40);
        Span<byte> bobOneTimePreKeyPub = stackalloc byte[32];
        bobOneTimePreKeyPub.Fill(140);
        var bobOpkPub = DhPublicKey.FromSpan(bobOneTimePreKeyPub);

        var bobBundle = new PreKeyBundle(
            PublicIdentityId.New(),
            DeviceId.Primary,
            bobIdPub,
            bobSpkPub,
            DeviceLinkProof.FromSpan(new byte[64]),
            bobOpkPub,
            OneTimePreKeyId: 42);

        // Act: Alice initiates
        var initiatorResult = X3dhAgreement.Initiate(
            aliceIdentityPriv,
            aliceIdPub,
            bobBundle,
            _cryptoEngine);

        Assert.That(initiatorResult.IsSuccess, Is.True);
        Assert.That(initiatorResult.Value.OneTimePreKeyIdUsed, Is.EqualTo(42u));

        // Act: Bob receives
        var receiverResult = X3dhAgreement.Receive(
            bobIdentityPriv,
            bobSignedPreKeyPriv,
            bobOneTimePreKeyPriv,
            initiatorResult.Value.InitiatorIdentityKey,
            initiatorResult.Value.EphemeralPublicKey,
            _cryptoEngine);

        // Assert: Both master secrets match exactly
        Assert.That(receiverResult.IsSuccess, Is.True);
        Assert.That(receiverResult.Value.Span.ToArray(), Is.EqualTo(initiatorResult.Value.MasterSecret.Span.ToArray()));
    }

    [Test]
    public void X3dh_WithoutOneTimePreKey_DerivesIdenticalMasterSecret()
    {
        // Arrange
        Span<byte> aliceIdentityPriv = stackalloc byte[32];
        aliceIdentityPriv.Fill(10);
        Span<byte> aliceIdentityPub = stackalloc byte[32];
        aliceIdentityPub.Fill(110);
        var aliceIdPub = IdentityKey.FromSpan(aliceIdentityPub);

        Span<byte> bobIdentityPriv = stackalloc byte[32];
        bobIdentityPriv.Fill(20);
        Span<byte> bobIdentityPub = stackalloc byte[32];
        bobIdentityPub.Fill(120);

        Span<byte> bobSignedPreKeyPriv = stackalloc byte[32];
        bobSignedPreKeyPriv.Fill(30);
        Span<byte> bobSignedPreKeyPub = stackalloc byte[32];
        bobSignedPreKeyPub.Fill(130);

        var bobBundle = new PreKeyBundle(
            PublicIdentityId.New(),
            DeviceId.Primary,
            IdentityKey.FromSpan(bobIdentityPub),
            DhPublicKey.FromSpan(bobSignedPreKeyPub),
            DeviceLinkProof.FromSpan(new byte[64]),
            OneTimePreKey: null,
            OneTimePreKeyId: 0);

        // Act
        var initiatorResult = X3dhAgreement.Initiate(aliceIdentityPriv, aliceIdPub, bobBundle, _cryptoEngine);
        var receiverResult = X3dhAgreement.Receive(
            bobIdentityPriv,
            bobSignedPreKeyPriv,
            ReadOnlySpan<byte>.Empty,
            initiatorResult.Value.InitiatorIdentityKey,
            initiatorResult.Value.EphemeralPublicKey,
            _cryptoEngine);

        // Assert
        Assert.That(initiatorResult.IsSuccess, Is.True);
        Assert.That(receiverResult.IsSuccess, Is.True);
        Assert.That(initiatorResult.Value.OneTimePreKeyIdUsed, Is.Null);
        Assert.That(receiverResult.Value.Span.ToArray(), Is.EqualTo(initiatorResult.Value.MasterSecret.Span.ToArray()));
    }

    [Test]
    public void X3dh_WhenSignedPreKeySignatureInvalid_ReturnsError()
    {
        // Arrange
        _cryptoEngine.SignaturesAlwaysValid = false;

        var bobBundle = new PreKeyBundle(
            PublicIdentityId.New(),
            DeviceId.Primary,
            IdentityKey.FromSpan(new byte[32]),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));

        // Act
        var result = X3dhAgreement.Initiate(new byte[32], IdentityKey.FromSpan(new byte[32]), bobBundle, _cryptoEngine);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error.Code, Is.EqualTo("INVALID_SIGNED_PREKEY_SIGNATURE"));
    }
}
