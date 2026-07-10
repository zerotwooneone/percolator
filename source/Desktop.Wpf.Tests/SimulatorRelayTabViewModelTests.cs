using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Network;


namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorRelayTabViewModelTests
{
    [Test]
    public void GlobalAutoRelayAll_sets_AutoDeliverEnabled_on_all_relays()
    {
        // Arrange
        var state = new StateStub();
        state.AddRelay(new NetworkPeerId(123456789), autoDeliverEnabled: false);
        state.AddRelay(new NetworkPeerId(987654321), autoDeliverEnabled: false);

        var delivery = Mock.Of<ISimulatorRelayDeliveryService>();
        var diagnostics = Mock.Of<ISimulatorDiagnosticsService>();
        var ui = new TestUiDispatcher();
        var logger = Mock.Of<ILogger<SimulatorRelayTabViewModel>>();
        var loggerFactory = Mock.Of<ILoggerFactory>();

        using var sut = new SimulatorRelayTabViewModel(
            state: state,
            delivery: delivery,
            diagnostics: diagnostics,
            ui: ui,
            logger: logger,
            loggerFactory: loggerFactory);

        // Act
        sut.GlobalAutoRelayAll.Value = true;

        // Assert: verify all relays have auto-deliver enabled (public behavior)
        state.Relays.Should().OnlyContain(r => r.AutoDeliverEnabled.CurrentValue);
    }
}
