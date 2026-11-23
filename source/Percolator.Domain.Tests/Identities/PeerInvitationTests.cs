using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public sealed class PeerInvitationTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;
    private PublicIdentityId _inviterId;
    private IdentityKey _inviterKey = null!;
    private RelayEndpoint _relayEndpoint = null!;
    private BlindedRoutingToken _routingToken;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
        _inviterId = PublicIdentityId.New();
        _inviterKey = IdentityKey.FromSpan(new byte[32]);
        _relayEndpoint = RelayEndpoint.Parse("percolator://relay.example.com:5100");
        _routingToken = BlindedRoutingToken.New();
    }

    [Test]
    public void Create_WithInvalidIdentityIdOrNullIdentityKey_FailsValidation()
    {
        var emptyIdResult = PeerInvitation.Create(default, _inviterKey, _relayEndpoint, _routingToken);
        emptyIdResult.IsFailure.Should().BeTrue();
        emptyIdResult.Error.Code.Should().Be("INVALID_IDENTITY_ID");

        var nullKeyResult = PeerInvitation.Create(_inviterId, null!, _relayEndpoint, _routingToken);
        nullKeyResult.IsFailure.Should().BeTrue();
        nullKeyResult.Error.Code.Should().Be("MISSING_IDENTITY_KEY");
    }

    [Test]
    public void Create_WithNullRelayEndpointOrEmptyRoutingToken_FailsValidation()
    {
        var nullRelayResult = PeerInvitation.Create(_inviterId, _inviterKey, null!, _routingToken);
        nullRelayResult.IsFailure.Should().BeTrue();
        nullRelayResult.Error.Code.Should().Be("MISSING_RELAY_ENDPOINT");

        var emptyTokenResult = PeerInvitation.Create(_inviterId, _inviterKey, _relayEndpoint, default);
        emptyTokenResult.IsFailure.Should().BeTrue();
        emptyTokenResult.Error.Code.Should().Be("INVALID_ROUTING_TOKEN");
    }

    [Test]
    public void VerifyFetchedBundle_MatchingPinnedKeyAndValidSpkSignature_ReturnsSuccess()
    {
        var invitation = PeerInvitation.Create(_inviterId, _inviterKey, _relayEndpoint, _routingToken).Value!;
        var bundle = new PreKeyBundle(
            DeviceId.Primary,
            _inviterKey,
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First);

        _cryptoEngine.SignaturesAlwaysValid = true;
        var result = invitation.VerifyFetchedBundle(bundle, _cryptoEngine);

        result.IsSuccess.Should().BeTrue();
    }

    [Test]
    public void VerifyFetchedBundle_MismatchedIdentityKey_ReturnsIdentityKeyMismatchFailure()
    {
        var invitation = PeerInvitation.Create(_inviterId, _inviterKey, _relayEndpoint, _routingToken).Value!;
        var differentKeyBytes = new byte[32];
        differentKeyBytes[0] = 0xFF;
        var differentKey = IdentityKey.FromSpan(differentKeyBytes);

        var bundle = new PreKeyBundle(
            DeviceId.Primary,
            differentKey,
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First);

        var result = invitation.VerifyFetchedBundle(bundle, _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IDENTITY_KEY_MISMATCH");
    }

    [Test]
    public void VerifyFetchedBundle_TamperedSpkSignature_ReturnsInvalidPreKeySignatureFailure()
    {
        var invitation = PeerInvitation.Create(_inviterId, _inviterKey, _relayEndpoint, _routingToken).Value!;
        var bundle = new PreKeyBundle(
            DeviceId.Primary,
            _inviterKey,
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]),
            SignedPreKeyId.First);

        _cryptoEngine.SignaturesAlwaysValid = false;
        var result = invitation.VerifyFetchedBundle(bundle, _cryptoEngine);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_PREKEY_SIGNATURE");
    }
}
