using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

namespace Percolator.Domain.Tests.Channels;

[TestFixture]
public sealed class ChannelIdTests
{
    [Test]
    public void DeterministicForDirect_SamePeersDifferentExchanges_ProducesDistinctChannelIds()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var exchange1 = RelayEndpoint.Parse("percolator://friends.exchange.com:5100");
        var exchange2 = RelayEndpoint.Parse("percolator://work.exchange.com:5100");

        var channelId1 = ChannelId.DeterministicForDirect(alice, bob, exchange1);
        var channelId2 = ChannelId.DeterministicForDirect(alice, bob, exchange2);

        channelId1.Should().NotBe(channelId2, "Channels scoped to different Exchanges must have distinct ChannelIds.");

        // Invariance under participant order
        var channelId1Reversed = ChannelId.DeterministicForDirect(bob, alice, exchange1);
        channelId1Reversed.Should().Be(channelId1, "ChannelId derivation must be symmetric with respect to participant ordering.");
    }
}
