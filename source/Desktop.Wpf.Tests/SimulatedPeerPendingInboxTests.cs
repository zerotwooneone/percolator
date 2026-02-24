using System;
using NUnit.Framework;
using Desktop.Wpf.Features.Simulator;
using Percolator.Contracts;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerPendingInboxTests
{
    [Test]
    public void Add_then_take_round_trips_for_same_peer_and_correlation()
    {
        var inbox = new SimulatedPeerPendingInbox();

        var peer = Guid.NewGuid();
        var corr = Guid.NewGuid();
        var msg = new InviteHandshakeResponse { Version = 1, RequestCorrelationId = corr.ToString() };

        inbox.AddInviteHandshakeResponse(peer, corr, msg);

        Assert.That(inbox.TryTakeInviteHandshakeResponse(peer, corr, out var taken), Is.True);
        Assert.That(taken.RequestCorrelationId, Is.EqualTo(corr.ToString()));

        Assert.That(inbox.TryTakeInviteHandshakeResponse(peer, corr, out _), Is.False);
    }

    [Test]
    public void TryGet_is_non_destructive()
    {
        var inbox = new SimulatedPeerPendingInbox();

        var peer = Guid.NewGuid();
        var corr = Guid.NewGuid();
        var msg = new InviteHandshakeResponse { Version = 1, RequestCorrelationId = corr.ToString() };

        inbox.AddInviteHandshakeResponse(peer, corr, msg);

        Assert.That(inbox.TryGetInviteHandshakeResponse(peer, corr, out var got), Is.True);
        Assert.That(got.RequestCorrelationId, Is.EqualTo(corr.ToString()));

        Assert.That(inbox.TryTakeInviteHandshakeResponse(peer, corr, out _), Is.True);
    }

    [Test]
    public void TryGet_returns_false_when_missing()
    {
        var inbox = new SimulatedPeerPendingInbox();

        Assert.That(inbox.TryGetInviteHandshakeResponse(Guid.NewGuid(), Guid.NewGuid(), out _), Is.False);
    }

    [Test]
    public void Taking_wrong_peer_or_correlation_does_not_remove_item()
    {
        var inbox = new SimulatedPeerPendingInbox();

        var peer = Guid.NewGuid();
        var corr = Guid.NewGuid();
        var msg = new InviteHandshakeResponse { Version = 1, RequestCorrelationId = corr.ToString() };

        inbox.AddInviteHandshakeResponse(peer, corr, msg);

        Assert.That(inbox.TryTakeInviteHandshakeResponse(Guid.NewGuid(), corr, out _), Is.False);
        Assert.That(inbox.TryTakeInviteHandshakeResponse(peer, Guid.NewGuid(), out _), Is.False);

        Assert.That(inbox.TryTakeInviteHandshakeResponse(peer, corr, out _), Is.True);
    }
}
