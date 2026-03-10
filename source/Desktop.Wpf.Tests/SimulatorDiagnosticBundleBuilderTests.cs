using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorDiagnosticBundleBuilderTests
{
    [Test]
    public async Task BuildJsonAsync_ContainsRequiredTopLevelSections()
    {
        // Arrange
        var relayHostId = Guid.NewGuid();
        var peerId = Guid.NewGuid();

        var peers = new ObservableCollection<SimulatedPeerDto>
        {
            new()
            {
                PeerId = relayHostId,
                DisplayName = "Relay",
                IsOnline = true,
                Relay = new SimulatedPeerRelayStateDto
                {
                    IsRelayCapable = true,
                    OpaqueQueue = new SimulatedRelayOpaqueQueueDto { Items = new() },
                    PreKeyStore = new SimulatedRelayPreKeyStoreDto { PublishedBundles = new() }
                },
                ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
            },
            new()
            {
                PeerId = peerId,
                DisplayName = "Peer",
                IsOnline = true,
                Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto()
            }
        };

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers).Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(peers));
        state.Setup(s => s.TryGetRuntimeStoreAsync(relayHostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SimulatedPeerRuntimeStoreDto
            {
                Version = 1,
                Sessions = new()
                {
                    new SimulatedSecureSessionDto
                    {
                        SessionId = Guid.NewGuid(),
                        RemotePeerId = peerId,
                        ProtocolVersion = 1,
                        RootKey = new byte[] { 1, 2, 3 },
                        SendCounter = 7,
                        RecvCounter = 8,
                        SkippedKeysCount = 0,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        LastUsedAtUtc = DateTimeOffset.UtcNow
                    }
                }
            });
        state.Setup(s => s.TryGetRuntimeStoreAsync(peerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);

        var diagnostics = new SimulatorDiagnosticsService();
        diagnostics.Emit(SimulatorDiagnosticEventType.PeerCreated, "Peer created", peerId: peerId);

        var sut = new SimulatorDiagnosticBundleBuilder(diagnostics, state.Object);

        // Act
        var json = await sut.BuildJsonAsync(CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("Peers", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("RelayQueueSummary", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("RecentEvents", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("SessionSummaries", out _).Should().BeTrue();
    }
}
