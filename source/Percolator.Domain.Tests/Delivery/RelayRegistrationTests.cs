using NUnit.Framework;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public sealed class RelayRegistrationTests
{
    private FakeDateTimeProvider _timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider();
    }

    [Test]
    public void DeliveryToken_GenerateRandom_ProducesNonZeroDistinct32ByteTokens()
    {
        var token1 = DeliveryToken.GenerateRandom();
        var token2 = DeliveryToken.GenerateRandom();

        Assert.That(token1.Length, Is.EqualTo(32));
        Assert.That(token1.ToArray().Any(b => b != 0), Is.True, "Token must not be all zero bytes.");
        Assert.That(token1.ToArray(), Is.Not.EqualTo(token2.ToArray()), "Two CSPRNG draws must be distinct.");
    }

    [Test]
    public void Create_ValidParameters_ReturnsRegistrationWithNoBindings()
    {
        var relayId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("relay.example.com", 5000);

        var result = RelayRegistration.Create(relayId, endpoint, null, _timeProvider);

        Assert.That(result.IsSuccess, Is.True);
        var registration = result.Value!;
        Assert.That(registration.Id, Is.EqualTo(relayId));
        Assert.That(registration.Endpoint, Is.EqualTo(endpoint));
        Assert.That(registration.PinnedCertificate, Is.Null);
        Assert.That(registration.CreatedAtUtc, Is.EqualTo(_timeProvider.UtcNow));
        Assert.That(registration.Bindings, Is.Empty);
    }

    [Test]
    public void Create_InvalidRelayId_Fails()
    {
        var result = RelayRegistration.Create(
            new PublicIdentityId(Guid.Empty),
            RelayEndpoint.FromHostAndPort("relay.example.com", 5000),
            null,
            _timeProvider);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Code, Is.EqualTo("INVALID_RELAY_ID"));
    }

    [Test]
    public void LinkIdentity_NewOwner_GeneratesCsprngTokens()
    {
        var registration = CreateRegistration();
        var ownerId = PublicIdentityId.New();

        var result = registration.LinkIdentity(ownerId, _timeProvider);

        Assert.That(result.IsSuccess, Is.True);
        var binding = result.Value!;
        Assert.That(binding.OwnerIdentityId, Is.EqualTo(ownerId));
        Assert.That(binding.RoutingToken.IsValid, Is.True);
        Assert.That(binding.DrainToken.ToArray().Any(b => b != 0), Is.True);
        Assert.That(binding.LinkedAtUtc, Is.EqualTo(_timeProvider.UtcNow));
        Assert.That(binding.PreviousDrainToken, Is.Null);
        Assert.That(registration.Bindings, Has.Count.EqualTo(1));
    }

    [Test]
    public void LinkIdentity_AlreadyLinked_IsIdempotentAndPreservesTokens()
    {
        var registration = CreateRegistration();
        var ownerId = PublicIdentityId.New();

        var first = registration.LinkIdentity(ownerId, _timeProvider).Value!;
        var second = registration.LinkIdentity(ownerId, _timeProvider).Value!;

        Assert.That(second.RoutingToken, Is.EqualTo(first.RoutingToken));
        Assert.That(second.DrainToken.ToArray(), Is.EqualTo(first.DrainToken.ToArray()));
        Assert.That(registration.Bindings, Has.Count.EqualTo(1));
    }

    [Test]
    public void LinkIdentity_InvalidOwnerId_Fails()
    {
        var registration = CreateRegistration();

        var result = registration.LinkIdentity(new PublicIdentityId(Guid.Empty), _timeProvider);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Code, Is.EqualTo("INVALID_OWNER_ID"));
    }

    [Test]
    public void UnlinkIdentity_LinkedOwner_RemovesBinding()
    {
        var registration = CreateRegistration();
        var ownerId = PublicIdentityId.New();
        registration.LinkIdentity(ownerId, _timeProvider);

        var result = registration.UnlinkIdentity(ownerId);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(registration.Bindings, Is.Empty);
    }

    [Test]
    public void UnlinkIdentity_NotLinked_IsNoOpSuccess()
    {
        var registration = CreateRegistration();

        var result = registration.UnlinkIdentity(PublicIdentityId.New());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(registration.Bindings, Is.Empty);
    }

    [Test]
    public void RotateDrainToken_LinkedOwner_RotatesTokenAndRetainsPrevious()
    {
        var registration = CreateRegistration();
        var ownerId = PublicIdentityId.New();
        var original = registration.LinkIdentity(ownerId, _timeProvider).Value!;

        var result = registration.RotateDrainToken(ownerId);

        Assert.That(result.IsSuccess, Is.True);
        var rotated = result.Value!;
        Assert.That(rotated.DrainToken.ToArray(), Is.Not.EqualTo(original.DrainToken.ToArray()));
        Assert.That(rotated.PreviousDrainToken, Is.Not.Null);
        Assert.That(rotated.PreviousDrainToken!.ToArray(), Is.EqualTo(original.DrainToken.ToArray()));
        Assert.That(rotated.RoutingToken, Is.EqualTo(original.RoutingToken));
    }

    [Test]
    public void RotateDrainToken_NotLinked_Fails()
    {
        var registration = CreateRegistration();

        var result = registration.RotateDrainToken(PublicIdentityId.New());

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Code, Is.EqualTo("MAILBOX_BINDING_NOT_FOUND"));
    }

    [Test]
    public void UpdateEndpoint_UpdatesEndpointOnly_PreservesPin()
    {
        var registration = CreateRegistration();
        var pin = CertificatePin.FromSpan(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        registration.SetCertificatePin(pin);
        var newEndpoint = RelayEndpoint.FromHostAndPort("other.example.com", 6000);

        var result = registration.UpdateEndpoint(newEndpoint);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(registration.Endpoint, Is.EqualTo(newEndpoint));
        Assert.That(registration.PinnedCertificate, Is.EqualTo(pin), "UpdateEndpoint must not modify the pin.");
    }

    [Test]
    public void SetCertificatePin_ValidPin_SetsPin()
    {
        var registration = CreateRegistration();
        var pin = CertificatePin.FromSpan(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var result = registration.SetCertificatePin(pin);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(registration.PinnedCertificate, Is.EqualTo(pin));
    }

    [Test]
    public void CertificatePin_InvalidLength_IsUnrepresentable()
    {
        Assert.Throws<ArgumentException>(() => CertificatePin.FromSpan(new byte[16]));
        Assert.That(CertificatePin.TryFromSpan(new byte[10], out var pin), Is.False);
        Assert.That(pin, Is.Null);
    }

    [Test]
    public void ClearCertificatePin_RemovesPin()
    {
        var registration = CreateRegistration();
        registration.SetCertificatePin(CertificatePin.FromSpan(new byte[32]));

        var result = registration.ClearCertificatePin();

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(registration.PinnedCertificate, Is.Null);
    }

    [Test]
    public void Rehydrate_InvalidArguments_Throws()
    {
        var endpoint = RelayEndpoint.FromHostAndPort("relay.example.com", 5000);

        Assert.Throws<ArgumentException>(() => RelayRegistration.Rehydrate(
            new PublicIdentityId(Guid.Empty), endpoint, null, _timeProvider.UtcNow, []));
        Assert.Throws<ArgumentNullException>(() => RelayRegistration.Rehydrate(
            PublicIdentityId.New(), endpoint, null, _timeProvider.UtcNow, null!));
    }

    [Test]
    public void Rehydrate_RestoresStateAndBindings()
    {
        var relayId = PublicIdentityId.New();
        var ownerId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("relay.example.com", 5000);
        var pin = CertificatePin.FromSpan(new byte[32]);
        var createdAt = _timeProvider.UtcNow;
        var binding = new RelayMailboxBinding(
            ownerId,
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            createdAt,
            DeliveryToken.GenerateRandom());

        var registration = RelayRegistration.Rehydrate(relayId, endpoint, pin, createdAt, new[] { binding });

        Assert.That(registration.Id, Is.EqualTo(relayId));
        Assert.That(registration.Endpoint, Is.EqualTo(endpoint));
        Assert.That(registration.PinnedCertificate, Is.EqualTo(pin));
        Assert.That(registration.CreatedAtUtc, Is.EqualTo(createdAt));
        Assert.That(registration.Bindings, Has.Count.EqualTo(1));
        var restored = registration.Bindings.Single();
        Assert.That(restored.OwnerIdentityId, Is.EqualTo(ownerId));
        Assert.That(restored.RoutingToken, Is.EqualTo(binding.RoutingToken));
        Assert.That(restored.DrainToken.ToArray(), Is.EqualTo(binding.DrainToken.ToArray()));
        Assert.That(restored.PreviousDrainToken!.ToArray(), Is.EqualTo(binding.PreviousDrainToken!.ToArray()));
    }

    private RelayRegistration CreateRegistration()
    {
        var result = RelayRegistration.Create(
            PublicIdentityId.New(),
            RelayEndpoint.FromHostAndPort("relay.example.com", 5000),
            null,
            _timeProvider);
        Assert.That(result.IsSuccess, Is.True);
        return result.Value!;
    }
}
