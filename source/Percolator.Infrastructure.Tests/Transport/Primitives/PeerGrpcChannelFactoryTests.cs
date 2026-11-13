namespace Percolator.Infrastructure.Tests.Transport.Primitives;

using System;
using System.Net.Http;
using System.Security.Authentication;
using FluentAssertions;
using Grpc.Net.Client;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class PeerGrpcChannelFactoryTests
{
    private sealed class MutableTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public void IdleChannel_EvictedAfterSlidingExpiration()
    {
        var timeProvider = new MutableTimeProvider();
        var options = new PeerGrpcChannelFactoryOptions
        {
            MaxCapacity = 3,
            IdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };

        var factory = new PeerGrpcChannelFactory(
            options,
            timeProvider);

        var uri1 = new Uri("https://1.1.1.1:5000");
        var uri2 = new Uri("https://2.2.2.2:5000");
        var uri3 = new Uri("https://3.3.3.3:5000");

        var ch1 = factory.GetOrCreateChannel(uri1);
        ch1.Should().NotBeNull();
        factory.ActiveChannelCount.Should().Be(1);

        // Same endpoint reuses existing channel
        var ch1Again = factory.GetOrCreateChannel(uri1);
        ch1Again.Should().BeSameAs(ch1);
        factory.ActiveChannelCount.Should().Be(1);

        var ch2 = factory.GetOrCreateChannel(uri2);
        var ch3 = factory.GetOrCreateChannel(uri3);
        factory.ActiveChannelCount.Should().Be(3);

        // Advance time by 6 minutes -> all 3 idle channels expire on next sweep
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);

        int evicted = factory.EvictIdleChannels();
        evicted.Should().Be(3);
        factory.ActiveChannelCount.Should().Be(0);

        // Adding beyond capacity evicts least recently used
        var freshCh1 = factory.GetOrCreateChannel(uri1);
        timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(10);
        var freshCh2 = factory.GetOrCreateChannel(uri2);
        timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(10);
        var freshCh3 = factory.GetOrCreateChannel(uri3);
        factory.ActiveChannelCount.Should().Be(3);

        // ch1 was accessed earliest, now add uri4
        timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(10);
        var uri4 = new Uri("https://4.4.4.4:5000");
        var ch4 = factory.GetOrCreateChannel(uri4);

        factory.ActiveChannelCount.Should().Be(3);

        factory.Dispose();
        factory.ActiveChannelCount.Should().Be(0);
    }

    [Test]
    public void InvalidateChannel_DisposesAndEvictsChannel()
    {
        var uri = new Uri("https://127.0.0.1:5050");
        using var factory = new PeerGrpcChannelFactory(new PeerGrpcChannelFactoryOptions(), new MutableTimeProvider());

        var channel1 = factory.GetOrCreateChannel(uri);
        factory.ActiveChannelCount.Should().Be(1);

        factory.InvalidateChannel(uri);
        factory.ActiveChannelCount.Should().Be(0);

        var channel2 = factory.GetOrCreateChannel(uri);
        factory.ActiveChannelCount.Should().Be(1);
        channel2.Should().NotBeSameAs(channel1);
    }

    [Test]
    public void ChannelCreation_ConfiguresTls13AndBlindValidationCallback()
    {
        GrpcChannelOptions? capturedOptions = null;
        var uri = new Uri("https://127.0.0.1:5050");

        using var factory = new PeerGrpcChannelFactory(
            new PeerGrpcChannelFactoryOptions(),
            new MutableTimeProvider(),
            (addr, opts) =>
            {
                capturedOptions = opts;
                return GrpcChannel.ForAddress(addr, opts);
            });

        factory.GetOrCreateChannel(uri);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.HttpHandler.Should().BeOfType<SocketsHttpHandler>();

        var handler = (SocketsHttpHandler)capturedOptions.HttpHandler!;
        handler.SslOptions.EnabledSslProtocols.Should().Be(SslProtocols.Tls13);
        handler.SslOptions.RemoteCertificateValidationCallback.Should().NotBeNull();
        handler.SslOptions.RemoteCertificateValidationCallback!(null!, null!, null!, default)
            .Should().BeTrue();
    }
}
