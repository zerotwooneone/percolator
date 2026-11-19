using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Topology;

[TestFixture]
public sealed class LoopbackPortAllocatorTests
{
    [SetUp]
    [TearDown]
    public void CleanUp()
    {
        LoopbackPortAllocator.Reset();
    }

    [Test]
    public void AllocateAvailablePort_ReturnsPortWithinCanonicalRange()
    {
        var port = LoopbackPortAllocator.AllocateAvailablePort();

        port.Should().BeInRange(LoopbackPortAllocator.RangeStart, LoopbackPortAllocator.RangeEnd);
        LoopbackPortAllocator.IsPortAllocated(port).Should().BeTrue();
    }

    [Test]
    public void AllocateAvailablePort_SequentialAllocations_YieldDistinctPortsWithinRange()
    {
        var allocated = new HashSet<int>();

        for (int i = 0; i < 5; i++)
        {
            var port = LoopbackPortAllocator.AllocateAvailablePort();
            port.Should().BeInRange(LoopbackPortAllocator.RangeStart, LoopbackPortAllocator.RangeEnd);
            allocated.Add(port).Should().BeTrue($"Port {port} was already allocated");
        }

        allocated.Count.Should().Be(5);
    }

    [Test]
    public void AllocateAvailablePort_WithPreferredPort_ReturnsPreferredPortWhenAvailable()
    {
        var preferred = 5150;
        var port = LoopbackPortAllocator.AllocateAvailablePort(preferred);

        port.Should().Be(preferred);
        LoopbackPortAllocator.IsPortAllocated(preferred).Should().BeTrue();
    }

    [Test]
    public void ReleasePort_AllowsPortToBeReallocated()
    {
        var port = LoopbackPortAllocator.AllocateAvailablePort();
        LoopbackPortAllocator.IsPortAllocated(port).Should().BeTrue();

        LoopbackPortAllocator.ReleasePort(port);
        LoopbackPortAllocator.IsPortAllocated(port).Should().BeFalse();

        var reallocated = LoopbackPortAllocator.AllocateAvailablePort(port);
        reallocated.Should().Be(port);
    }

    [Test]
    public void Reset_ClearsAllAllocations()
    {
        var port1 = LoopbackPortAllocator.AllocateAvailablePort();
        var port2 = LoopbackPortAllocator.AllocateAvailablePort();

        LoopbackPortAllocator.IsPortAllocated(port1).Should().BeTrue();
        LoopbackPortAllocator.IsPortAllocated(port2).Should().BeTrue();

        LoopbackPortAllocator.Reset();

        LoopbackPortAllocator.IsPortAllocated(port1).Should().BeFalse();
        LoopbackPortAllocator.IsPortAllocated(port2).Should().BeFalse();
    }
}
