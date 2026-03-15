using System.Linq;
using System.Threading;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class SimulatorDiagnosticsServiceTests
{
    [SetUp]
    public void SetUp() => WpfTestHarness.EnsureApplication();

    [Test]
    public void Emit_MaintainsBoundedBuffer()
    {
        // Arrange
        var sut = new SimulatorDiagnosticsService();

        // Act
        for (var i = 0; i < 2100; i++)
        {
            sut.Emit(SimulatorDiagnosticEventType.PeerCreated, $"ev-{i}");
        }

        // Assert
        sut.Events.Count.Should().Be(2000);
        sut.Events.First().Message.Should().Be("ev-100");
        sut.Events.Last().Message.Should().Be("ev-2099");
    }

    [Test]
    public void Clear_EmptiesEvents()
    {
        // Arrange
        var sut = new SimulatorDiagnosticsService();
        sut.Emit(SimulatorDiagnosticEventType.PeerCreated, "a");
        sut.Emit(SimulatorDiagnosticEventType.PeerRemoved, "b");

        // Act
        sut.Clear();

        // Assert
        sut.Events.Should().BeEmpty();
    }
}
