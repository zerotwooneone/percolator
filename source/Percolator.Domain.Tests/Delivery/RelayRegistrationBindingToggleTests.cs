using System;
using System.Linq;
using NUnit.Framework;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public sealed class RelayRegistrationBindingToggleTests
{
    private FakeDateTimeProvider _timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider();
    }

    [Test]
    public void DefaultBindingStatus_IsEnabled()
    {
        var relayId = PublicIdentityId.New();
        var ownerId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5102);

        var reg = RelayRegistration.Create(relayId, endpoint, null, _timeProvider).Value!;
        var linkResult = reg.LinkIdentity(ownerId, _timeProvider);

        Assert.That(linkResult.IsSuccess, Is.True);
        Assert.That(linkResult.Value!.Status, Is.EqualTo(RelayLinkStatus.Enabled));
        Assert.That(reg.Bindings.First().Status, Is.EqualTo(RelayLinkStatus.Enabled));
    }

    [Test]
    public void SetBindingStatus_UpdatesStatus_WhenBindingExists()
    {
        var relayId = PublicIdentityId.New();
        var ownerId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5102);

        var reg = RelayRegistration.Create(relayId, endpoint, null, _timeProvider).Value!;
        reg.LinkIdentity(ownerId, _timeProvider);

        var toggleResult = reg.SetBindingStatus(ownerId, RelayLinkStatus.Disabled);

        Assert.That(toggleResult.IsSuccess, Is.True);
        Assert.That(reg.Bindings.First().Status, Is.EqualTo(RelayLinkStatus.Disabled));

        // Re-enable
        var reEnableResult = reg.SetBindingStatus(ownerId, RelayLinkStatus.Enabled);
        Assert.That(reEnableResult.IsSuccess, Is.True);
        Assert.That(reg.Bindings.First().Status, Is.EqualTo(RelayLinkStatus.Enabled));
    }

    [Test]
    public void SetBindingStatus_ReturnsNotFoundError_WhenBindingDoesNotExist()
    {
        var relayId = PublicIdentityId.New();
        var absentOwnerId = PublicIdentityId.New();
        var endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5102);

        var reg = RelayRegistration.Create(relayId, endpoint, null, _timeProvider).Value!;

        var result = reg.SetBindingStatus(absentOwnerId, RelayLinkStatus.Disabled);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Error.Code, Is.EqualTo("MAILBOX_BINDING_NOT_FOUND"));
    }
}
