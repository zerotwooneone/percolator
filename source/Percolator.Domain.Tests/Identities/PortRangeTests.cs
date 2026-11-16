namespace Percolator.Domain.Tests.Identities;

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;

[TestFixture]
public sealed class PortRangeTests
{
    [Test]
    public void Constructor_ValidMinAndMax_SetsPropertiesCorrectly()
    {
        var range = new PortRange(5000, 6000);
        range.MinPort.Should().Be(5000);
        range.MaxPort.Should().Be(6000);
        range.IsSinglePort.Should().BeFalse();
    }

    [Test]
    public void Constructor_MinPortBelow1_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => new PortRange(0, 5000);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("minPort");

        Action actNeg = () => new PortRange(-1, 5000);
        actNeg.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("minPort");
    }

    [Test]
    public void Constructor_MaxPortAbove65535_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => new PortRange(5000, 65536);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxPort");
    }

    [Test]
    public void Constructor_MinGreaterThanMax_ThrowsArgumentException()
    {
        Action act = () => new PortRange(5001, 5000);
        act.Should().Throw<ArgumentException>()
            .WithParameterName("minPort");
    }

    [Test]
    public void Single_ValidPort_SetsEqualMinAndMax_AndIsSinglePortTrue()
    {
        var single = PortRange.Single(8080);
        single.MinPort.Should().Be(8080);
        single.MaxPort.Should().Be(8080);
        single.IsSinglePort.Should().BeTrue();
    }

    [Test]
    public void DefaultDynamicRange_Is5100To5299()
    {
        var defaultRange = PortRange.DefaultDynamicRange;
        defaultRange.MinPort.Should().Be(5100);
        defaultRange.MaxPort.Should().Be(5299);
        defaultRange.IsSinglePort.Should().BeFalse();
    }

    [Test]
    public void Contains_ReturnsTrueForPortsInRange_AndFalseOutside()
    {
        var range = new PortRange(5100, 5200);
        range.Contains(5099).Should().BeFalse();
        range.Contains(5100).Should().BeTrue();
        range.Contains(5150).Should().BeTrue();
        range.Contains(5200).Should().BeTrue();
        range.Contains(5201).Should().BeFalse();
    }

    [Test]
    public void ToString_SinglePort_ReturnsSingleNumber()
    {
        PortRange.Single(5100).ToString().Should().Be("5100");
    }

    [Test]
    public void ToString_Range_ReturnsHyphenatedRange()
    {
        new PortRange(5100, 5299).ToString().Should().Be("5100-5299");
    }

    [Test]
    public void RecordEquality_EqualRanges_AreEqual()
    {
        var range1 = new PortRange(5100, 5200);
        var range2 = new PortRange(5100, 5200);
        range1.Should().Be(range2);
    }
}
