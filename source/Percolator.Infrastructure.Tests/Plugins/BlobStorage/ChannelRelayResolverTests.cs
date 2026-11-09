namespace Percolator.Infrastructure.Tests.Plugins.BlobStorage;

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Infrastructure.Plugins.BlobStorage;

[TestFixture]
public sealed class ChannelRelayResolverTests
{
    [Test]
    public async Task ResolveRelayForChannelAsync_WhenNotRegistered_ReturnsDefaultUri()
    {
        // Arrange
        var defaultUri = new Uri("https://default.relay.net");
        var resolver = new ChannelRelayResolver(defaultUri);
        var channelId = ChannelId.New();

        // Act
        var resolved = await resolver.ResolveRelayForChannelAsync(channelId);

        // Assert
        resolved.Should().Be(defaultUri);
    }

    [Test]
    public async Task ResolveRelayForChannelAsync_WhenRegistered_ReturnsRegisteredUri()
    {
        // Arrange
        var defaultUri = new Uri("https://default.relay.net");
        var channelUri = new Uri("https://channel.relay.net");
        var resolver = new ChannelRelayResolver(defaultUri);
        var channelId = ChannelId.New();

        // Act
        resolver.RegisterRelayForChannel(channelId, channelUri);
        var resolved = await resolver.ResolveRelayForChannelAsync(channelId);

        // Assert
        resolved.Should().Be(channelUri);
    }

    [Test]
    public async Task RegisterRelayForChannel_OverwritingExisting_UpdatesUri()
    {
        // Arrange
        var initialUri = new Uri("https://initial.relay.net");
        var updatedUri = new Uri("https://updated.relay.net");
        var resolver = new ChannelRelayResolver();
        var channelId = ChannelId.New();

        // Act
        resolver.RegisterRelayForChannel(channelId, initialUri);
        resolver.RegisterRelayForChannel(channelId, updatedUri);
        var resolved = await resolver.ResolveRelayForChannelAsync(channelId);

        // Assert
        resolved.Should().Be(updatedUri);
    }

    [Test]
    public async Task ConcurrentAccess_HandlesParallelRegistrationsAndResolutions()
    {
        // Arrange
        var resolver = new ChannelRelayResolver(new Uri("https://default.relay.net"));
        var channels = Enumerable.Range(0, 100).Select(_ => ChannelId.New()).ToList();

        // Act
        var tasks = channels.Select(channelId => Task.Run(async () =>
        {
            var uri = new Uri($"https://node-{channelId.Value}.relay.net");
            resolver.RegisterRelayForChannel(channelId, uri);
            var resolved = await resolver.ResolveRelayForChannelAsync(channelId);
            resolved.Should().Be(uri);
        }));

        await Task.WhenAll(tasks);
    }
}
