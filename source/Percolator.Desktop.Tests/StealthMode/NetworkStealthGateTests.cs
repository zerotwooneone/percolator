using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Tests.StealthMode;

[TestFixture]
public sealed class NetworkStealthGateTests
{
    private NetworkStealthGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _gate = new NetworkStealthGate(NullLogger<NetworkStealthGate>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _gate.Dispose();
    }

    [Test]
    public void Startup_OperatesInTotalStealthMode_WithNoActiveIdentities()
    {
        _gate.IsStealthActive.Should().BeTrue("client must be in total stealth mode upon startup");
        _gate.ActiveIdentities.Should().BeEmpty("no identities should be active prior to explicit confirmation");
    }

    [Test]
    public async Task ActivateAsync_WithEmptyIdentities_ThrowsArgumentException()
    {
        var act = async () => await _gate.ActivateAsync(new List<PublicIdentityId>());
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one active identity is required*");

        _gate.IsStealthActive.Should().BeTrue("client must remain in stealth mode after invalid activation attempt");
    }

    [Test]
    public async Task ActivateAsync_WithValidIdentities_TransitionsOutStealthMode_AndEmitsActive()
    {
        var id1 = PublicIdentityId.New();
        var id2 = PublicIdentityId.New();

        var stateEmissions = new List<bool>();
        using var sub = _gate.NetworkActiveChanged.Subscribe(stateEmissions.Add);

        await _gate.ActivateAsync([id1, id2]);

        _gate.IsStealthActive.Should().BeFalse();
        _gate.ActiveIdentities.Should().HaveCount(2).And.Contain([id1, id2]);
        stateEmissions.Should().ContainInOrder([false, true]);
    }

    [Test]
    public async Task DeactivateAsync_RevertsToTotalStealthMode_ClearsIdentities_AndEmitsInactive()
    {
        var id = PublicIdentityId.New();
        await _gate.ActivateAsync([id]);

        var stateEmissions = new List<bool>();
        using var sub = _gate.NetworkActiveChanged.Subscribe(stateEmissions.Add);

        await _gate.DeactivateAsync();

        _gate.IsStealthActive.Should().BeTrue("deactivation must return node to total stealth");
        _gate.ActiveIdentities.Should().BeEmpty();
        stateEmissions.Should().ContainInOrder([true, false]);
    }

    [Test]
    public async Task ConcurrentActivations_AreSafelySerialized()
    {
        var id1 = PublicIdentityId.New();
        var id2 = PublicIdentityId.New();

        var t1 = Task.Run(() => _gate.ActivateAsync([id1]));
        var t2 = Task.Run(() => _gate.ActivateAsync([id2]));

        await Task.WhenAll(t1, t2);

        _gate.IsStealthActive.Should().BeFalse();
        _gate.ActiveIdentities.Should().NotBeEmpty();
    }
}
