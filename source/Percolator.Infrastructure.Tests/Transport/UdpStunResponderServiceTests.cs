namespace Percolator.Infrastructure.Tests.Transport;

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class UdpStunResponderServiceTests
{
    [Test]
    public void Rfc5389StunCodec_Ipv4BindingRequest_RoundtripsXorMappedAddress()
    {
        byte[] txId = new byte[12];
        RandomNumberGenerator.Fill(txId);

        byte[] request = new byte[Rfc5389StunCodec.HeaderSizeBytes];
        Rfc5389StunCodec.BuildBindingRequest(txId, request);

        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 54321);
        byte[] response = new byte[Rfc5389StunCodec.MaxResponseSizeBytes];

        bool built = Rfc5389StunCodec.TryBuildBindingResponse(request, remote, response, out int bytesWritten);
        built.Should().BeTrue();
        bytesWritten.Should().Be(32);

        bool parsed = Rfc5389StunCodec.TryParseBindingResponse(response.AsSpan(0, bytesWritten), txId, out IPEndPoint? mapped);
        parsed.Should().BeTrue();
        mapped.Should().Be(remote);
    }

    [Test]
    public void Rfc5389StunCodec_Ipv6BindingRequest_RoundtripsXorMappedAddress()
    {
        byte[] txId = new byte[12];
        RandomNumberGenerator.Fill(txId);

        byte[] request = new byte[Rfc5389StunCodec.HeaderSizeBytes];
        Rfc5389StunCodec.BuildBindingRequest(txId, request);

        var remote = new IPEndPoint(IPAddress.Parse("2001:db8::1234:5678"), 41234);
        byte[] response = new byte[Rfc5389StunCodec.MaxResponseSizeBytes];

        bool built = Rfc5389StunCodec.TryBuildBindingResponse(request, remote, response, out int bytesWritten);
        built.Should().BeTrue();
        bytesWritten.Should().Be(44);

        bool parsed = Rfc5389StunCodec.TryParseBindingResponse(response.AsSpan(0, bytesWritten), txId, out IPEndPoint? mapped);
        parsed.Should().BeTrue();
        mapped.Should().Be(remote);
    }

    [Test]
    public void Rfc5389StunCodec_RejectsMalformedOversizedAndInvalidCookiePackets()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 50000);
        byte[] response = new byte[Rfc5389StunCodec.MaxResponseSizeBytes];

        // Too short (< 20 bytes)
        Rfc5389StunCodec.TryBuildBindingResponse(new byte[19], remote, response, out _).Should().BeFalse();

        // Oversized (> 548 bytes)
        Rfc5389StunCodec.TryBuildBindingResponse(new byte[552], remote, response, out _).Should().BeFalse();

        // Wrong magic cookie
        byte[] badCookie = new byte[20];
        Rfc5389StunCodec.BuildBindingRequest(new byte[12], badCookie);
        badCookie[4] ^= 0xFF;
        Rfc5389StunCodec.TryBuildBindingResponse(badCookie, remote, response, out _).Should().BeFalse();

        // Wrong message type (0x0101 response instead of 0x0001 request)
        byte[] wrongType = new byte[20];
        Rfc5389StunCodec.BuildBindingRequest(new byte[12], wrongType);
        wrongType[0] = 0x01;
        wrongType[1] = 0x01;
        Rfc5389StunCodec.TryBuildBindingResponse(wrongType, remote, response, out _).Should().BeFalse();

        // Unaligned message length (not a multiple of 4)
        byte[] unaligned = new byte[22];
        Rfc5389StunCodec.BuildBindingRequest(new byte[12], unaligned);
        unaligned[3] = 2;
        Rfc5389StunCodec.TryBuildBindingResponse(unaligned, remote, response, out _).Should().BeFalse();
    }

    [Test]
    public async Task UdpStunResponderService_GatedBySwarmTrackerFlag_AnswersLoopbackBindingRequestsAndTogglesCleanly()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var policyHolder = new RelayHostingPolicyHolder(RelayHostingPolicy.Default); // SwarmTracker OFF by default
        var options = new UdpStunResponderOptions { BindAddress = IPAddress.Loopback, Port = 0 };

        await using var stunService = new UdpStunResponderService(policyHolder, options, clock);
        await stunService.StartAsync(CancellationToken.None);

        // 1. SwarmTracker disabled by default -> UDP socket must NOT bind
        stunService.IsRunning.Should().BeFalse();
        stunService.BoundEndpoint.Should().BeNull();

        // 2. Operator enables SwarmTracker -> UDP STUN responder binds dynamically
        var enabledPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);
        policyHolder.UpdatePolicy(enabledPolicy);

        stunService.IsRunning.Should().BeTrue();
        stunService.BoundEndpoint.Should().NotBeNull();
        IPEndPoint serverEp = stunService.BoundEndpoint!;

        // 3. Send real garbage datagram (must be silently dropped) followed by valid RFC 5389 Binding Request
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint clientLocalEp = (IPEndPoint)client.Client.LocalEndPoint!;

        await client.SendAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, serverEp);

        byte[] txId = new byte[12];
        RandomNumberGenerator.Fill(txId);
        byte[] request = new byte[Rfc5389StunCodec.HeaderSizeBytes];
        Rfc5389StunCodec.BuildBindingRequest(txId, request);

        await client.SendAsync(request, serverEp);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        UdpReceiveResult received = await client.ReceiveAsync(cts.Token);

        bool parsed = Rfc5389StunCodec.TryParseBindingResponse(received.Buffer, txId, out IPEndPoint? mapped);
        parsed.Should().BeTrue();
        mapped.Should().Be(clientLocalEp);

        // 4. Operator disables SwarmTracker -> UDP STUN responder unbinds cleanly
        policyHolder.UpdatePolicy(RelayHostingPolicy.Default);
        stunService.IsRunning.Should().BeFalse();
        stunService.BoundEndpoint.Should().BeNull();

        await stunService.StopAsync(CancellationToken.None);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public FixedClock(DateTimeOffset initial) => UtcNow = initial;
    }
}
