using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerDirectoryInitializationTests
{
    [Test]
    public async Task InitializeAsync_CoalescesConcurrentCalls_AndBuildsModelsFromDtos()
    {
        // Arrange
        var dto = new SimulatedPeerDto
        {
            PeerId = Guid.NewGuid(),
            DisplayName = "Alice",
            IsOnline = true,
            Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = true },
            ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
            {
                IdentitySigningKeySpki = new byte[] { 1, 2, 3 },
                IdentitySigningKeyPrivateKeyEcPrivateKey = new byte[] { 4, 5, 6 }
            }
        };

        var peers = new ObservableCollection<SimulatedPeerDto> { dto };
        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);

        var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initCalls = 0;
        state.SetupGet(s => s.Peers)
            .Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(peers));
        state.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref initCalls))
            .Returns(initGate.Task);

        state.Setup(s => s.TryGetPeerIdByIdentityPkhAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        state.Setup(s => s.AddRelayActiveSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        state.Setup(s => s.RemoveRelayActiveSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var sut = new SimulatedPeerDirectory(state.Object);

        // Act
        var t1 = sut.InitializeAsync();
        var t2 = sut.InitializeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref initCalls) == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(5, cts.Token);
        }

        initCalls.Should().Be(1);

        initGate.SetResult();
        await Task.WhenAll(t1, t2);

        // Assert
        initCalls.Should().Be(1);
        sut.Peers.Should().HaveCount(1);
        sut.Peers[0].PeerId.Should().Be(dto.PeerId);
        sut.Peers[0].DisplayName.CurrentValue.Should().Be("Alice");
        sut.Peers[0].IsRelayCapable.CurrentValue.Should().BeTrue();
    }
}
