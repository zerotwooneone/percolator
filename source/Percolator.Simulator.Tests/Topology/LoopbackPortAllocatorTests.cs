using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Transport;

namespace Percolator.Simulator.Tests.Topology;

[TestFixture]
public sealed class LoopbackPortAllocatorTests
{
    private ISimulatedPortReservationService _portReservationService = null!;

    [SetUp]
    public void SetUp()
    {
        _portReservationService = new PortReservationService();
    }

    [TearDown]
    public void CleanUp()
    {
        _portReservationService.Reset();
    }

    [Test]
    public void AllocateAvailablePort_ReturnsPortWithinCanonicalRange()
    {
        var port = _portReservationService.AllocateAvailablePort();

        port.Should().BeInRange(_portReservationService.RangeStart, _portReservationService.RangeEnd);
        _portReservationService.IsPortAllocated(port).Should().BeTrue();
    }

    [Test]
    public void AllocateAvailablePort_SequentialAllocations_YieldDistinctPortsWithinRange()
    {
        var allocated = new HashSet<int>();

        for (int i = 0; i < 5; i++)
        {
            var port = _portReservationService.AllocateAvailablePort();
            port.Should().BeInRange(_portReservationService.RangeStart, _portReservationService.RangeEnd);
            allocated.Add(port).Should().BeTrue($"Port {port} was already allocated");
        }

        allocated.Count.Should().Be(5);
    }

    [Test]
    public void AllocateAvailablePort_WithPreferredPort_ReturnsPreferredPortWhenAvailable()
    {
        var preferred = 5150;
        var port = _portReservationService.AllocateAvailablePort(preferred);

        port.Should().Be(preferred);
        _portReservationService.IsPortAllocated(preferred).Should().BeTrue();
    }

    [Test]
    public void ReleasePort_AllowsPortToBeReallocated()
    {
        var port = _portReservationService.AllocateAvailablePort();
        _portReservationService.IsPortAllocated(port).Should().BeTrue();

        _portReservationService.ReleasePort(port);
        _portReservationService.IsPortAllocated(port).Should().BeFalse();

        var reallocated = _portReservationService.AllocateAvailablePort(port);
        reallocated.Should().Be(port);
    }

    [Test]
    public void Reset_ClearsAllAllocations()
    {
        var port1 = _portReservationService.AllocateAvailablePort();
        var port2 = _portReservationService.AllocateAvailablePort();

        _portReservationService.IsPortAllocated(port1).Should().BeTrue();
        _portReservationService.IsPortAllocated(port2).Should().BeTrue();

        _portReservationService.Reset();

        _portReservationService.IsPortAllocated(port1).Should().BeFalse();
        _portReservationService.IsPortAllocated(port2).Should().BeFalse();
    }
}
