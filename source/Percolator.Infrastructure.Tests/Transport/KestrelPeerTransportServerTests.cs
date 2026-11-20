namespace Percolator.Infrastructure.Tests.Transport;

using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class KestrelPeerTransportServerTests
{
    private static IServiceProvider BuildTestServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
        return services.BuildServiceProvider();
    }

    private static IPAddress? GetFirstOperationalUnicastIPv4Address()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => u.Address)
            .FirstOrDefault();
    }

    [Test]
    public void Constructor_BeforeStart_PreservesNetworkStealthWithZeroOpenSockets()
    {
        var sp = BuildTestServiceProvider();
        var cert = sp.GetRequiredService<ITransportCertificateProvider>();
        var server = new KestrelPeerTransportServer(cert, sp, new PeerTransportServerOptions());

        server.IsRunning.Should().BeFalse();
        server.BoundPort.Should().BeNull();
        server.BoundAddress.Should().BeNull();
    }

    [Test]
    public async Task StartAsync_BindsToConfiguredOptionsBindAddressByDefault_AndAcceptsTcpConnections()
    {
        var sp = BuildTestServiceProvider();
        var cert = sp.GetRequiredService<ITransportCertificateProvider>();
        var options = new PeerTransportServerOptions { BindAddress = IPAddress.Any };
        await using var server = new KestrelPeerTransportServer(cert, sp, options);

        await server.StartAsync();

        server.IsRunning.Should().BeTrue();
        server.BoundPort.Should().NotBeNull();
        server.BoundPort!.Value.Value.Should().BeGreaterThan(0);
        server.BoundAddress.Should().Be(IPAddress.Any);

        // Verify wire reachability over loopback
        using var clientLoopback = new TcpClient();
        await clientLoopback.ConnectAsync(IPAddress.Loopback, server.BoundPort.Value.Value);
        clientLoopback.Connected.Should().BeTrue();

        // Verify wire reachability over LAN interface if available (multi-interface binding proof)
        var lanIp = GetFirstOperationalUnicastIPv4Address();
        if (lanIp != null)
        {
            using var clientLan = new TcpClient();
            await clientLan.ConnectAsync(lanIp, server.BoundPort.Value.Value);
            clientLan.Connected.Should().BeTrue();
        }

        await server.StopAsync();
        server.IsRunning.Should().BeFalse();
        server.BoundPort.Should().BeNull();
        server.BoundAddress.Should().BeNull();
    }

    [Test]
    public async Task StartAsync_WithExplicitLoopback_BindsToLoopbackOnly()
    {
        var sp = BuildTestServiceProvider();
        var cert = sp.GetRequiredService<ITransportCertificateProvider>();
        var options = new PeerTransportServerOptions { BindAddress = IPAddress.Any };
        await using var server = new KestrelPeerTransportServer(cert, sp, options);

        await server.StartAsync(IPAddress.Loopback, new PortNumber(0));

        server.IsRunning.Should().BeTrue();
        server.BoundPort.Should().NotBeNull();
        server.BoundAddress.Should().Be(IPAddress.Loopback);

        // Connect over loopback must succeed
        using var clientLoopback = new TcpClient();
        await clientLoopback.ConnectAsync(IPAddress.Loopback, server.BoundPort!.Value.Value);
        clientLoopback.Connected.Should().BeTrue();

        // Connect over non-loopback LAN IP must fail when listener is restricted to 127.0.0.1
        var lanIp = GetFirstOperationalUnicastIPv4Address();
        if (lanIp != null && !IPAddress.IsLoopback(lanIp))
        {
            using var clientLan = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            Func<Task> connectLan = async () => await clientLan.ConnectAsync(lanIp, server.BoundPort.Value.Value, cts.Token);
            await connectLan.Should().ThrowAsync<Exception>("listening on 127.0.0.1 must not accept traffic from external LAN address");
        }

        await server.StopAsync();
        server.IsRunning.Should().BeFalse();
        server.BoundPort.Should().BeNull();
        server.BoundAddress.Should().BeNull();
    }

    [Test]
    public async Task IHostedServiceStartAsync_BindsUsingConfiguredOptionsAddress()
    {
        var sp = BuildTestServiceProvider();
        var cert = sp.GetRequiredService<ITransportCertificateProvider>();
        var options = new PeerTransportServerOptions { BindAddress = IPAddress.Any };
        var server = new KestrelPeerTransportServer(cert, sp, options);
        IHostedService hostedService = server;

        await hostedService.StartAsync(CancellationToken.None);

        server.IsRunning.Should().BeTrue();
        server.BoundPort.Should().NotBeNull();
        server.BoundAddress.Should().Be(IPAddress.Any);

        await hostedService.StopAsync(CancellationToken.None);
        server.IsRunning.Should().BeFalse();
        server.BoundPort.Should().BeNull();
        server.BoundAddress.Should().BeNull();
    }

    [Test]
    public async Task StartAsync_WhenAlreadyRunning_IsIdempotent()
    {
        var sp = BuildTestServiceProvider();
        var cert = sp.GetRequiredService<ITransportCertificateProvider>();
        await using var server = new KestrelPeerTransportServer(cert, sp, new PeerTransportServerOptions());

        await server.StartAsync();
        var initialPort = server.BoundPort;
        var initialAddress = server.BoundAddress;

        // Second start call must be idempotent no-op
        await server.StartAsync();

        server.BoundPort.Should().Be(initialPort);
        server.BoundAddress.Should().Be(initialAddress);
        server.IsRunning.Should().BeTrue();

        await server.StopAsync();
    }
}
