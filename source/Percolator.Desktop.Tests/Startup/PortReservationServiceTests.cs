using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Transport;

namespace Percolator.Desktop.Tests.Startup;

[TestFixture]
[NonParallelizable] // binds real loopback sockets — must not race other socket fixtures
public sealed class PortReservationServiceTests
{
    private const int TestRangeStart = 5900;
    private const int TestRangeEnd = 5905;

    private PortReservationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new PortReservationService(TestRangeStart, TestRangeEnd);
    }

    private static TcpListener Hold(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    [Test]
    public async Task Acquire_PreferredFree_LeasesThatPort()
    {
        var lease = await _service.AcquireAsync(TestRangeStart);

        lease.Should().NotBeNull();
        lease!.Port.Should().Be(TestRangeStart);
        lease.Dispose();
    }

    [Test]
    public async Task Acquire_PreferredBusy_AutoSelectsNextFreePort()
    {
        using var held = Hold(TestRangeStart);

        var lease = await _service.AcquireAsync(TestRangeStart);

        lease.Should().NotBeNull();
        lease!.Port.Should().Be(TestRangeStart + 1,
            "a busy preferred port must cascade to the next free port in range");
        lease.Dispose();
    }

    [Test]
    public async Task Acquire_RangeExhausted_ReturnsNull()
    {
        using var first = Hold(TestRangeStart);
        using var second = Hold(TestRangeStart + 1);

        var lease = await new PortReservationService(TestRangeStart, TestRangeStart + 1)
            .AcquireAsync(TestRangeStart);

        lease.Should().BeNull("every port in the range is held by another process");
    }

    [Test]
    public async Task Probe_PreferredBusy_ReportsAutoSelected()
    {
        using var held = Hold(TestRangeStart);

        var probe = await _service.ProbeAsync(TestRangeStart);

        probe.Status.Should().Be(PortProbeStatus.AutoSelected);
        probe.Port.Should().Be(TestRangeStart + 1);
    }

    [Test]
    public async Task Probe_IsTransient_LeavesPreferredPortBindable()
    {
        var probe = await _service.ProbeAsync(TestRangeStart);
        probe.Status.Should().Be(PortProbeStatus.Ready);

        // The stealth invariant: probing must not retain a socket — a later bind succeeds.
        var lease = await _service.AcquireAsync(TestRangeStart);
        lease.Should().NotBeNull();
        lease!.Port.Should().Be(TestRangeStart);
        lease.Dispose();
    }

    [Test]
    public async Task Lease_Dispose_ReleasesThePort()
    {
        var lease = await _service.AcquireAsync(TestRangeStart);
        lease!.Dispose();

        var reacquired = await _service.AcquireAsync(TestRangeStart);
        reacquired.Should().NotBeNull(
            "disposing the lease must release the socket for the next reservation");
        reacquired!.Port.Should().Be(TestRangeStart);
        reacquired.Dispose();
    }

    [Test]
    public async Task Probe_PreferredNearEndAndUpperBusy_WrapsAroundToRangeStart()
    {
        // Test range is 5900–5905. Preferred is 5904. Hold 5904 and 5905.
        // It must wrap around and pick 5900.
        using var held4 = Hold(TestRangeStart + 4);
        using var held5 = Hold(TestRangeStart + 5);

        var probe = await _service.ProbeAsync(TestRangeStart + 4);

        probe.Status.Should().Be(PortProbeStatus.AutoSelected);
        probe.Port.Should().Be(TestRangeStart, "candidate search wraps around to range start when upper ports are busy");
    }

    [Test]
    public async Task ProbeNextAvailable_SkipsClaimedPorts_AutoSelectsNextFree()
    {
        // Claim 5900 and 5901 in vault (even though sockets are unbound)
        var claimed = new[] { TestRangeStart, TestRangeStart + 1 };

        var probe = await _service.ProbeNextAvailableAsync(TestRangeStart, claimed);

        probe.Status.Should().Be(PortProbeStatus.AutoSelected);
        probe.Port.Should().Be(TestRangeStart + 2, "must skip claimed ports and find the next free bindable port in range");
    }
}
