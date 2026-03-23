using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using ObservableCollections;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerDirectoryInitializationTests
{
    [Test]
    public async Task InitializeAsync_CoalescesConcurrentCalls_AndProjectsModelsFromState()
    {
        // Arrange
        var peerId = Guid.NewGuid();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportECPrivateKey();
        var spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var model = new SimulatedPeerModel(peerId, "Alice", isOnline: true, isRelayCapable: true, spki, priv);

        var peers = new ObservableList<SimulatedPeerModel>();
        peers.Add(model);
        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);

        var initGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initCalls = 0;
        state.SetupGet(s => s.Peers)
            .Returns(peers);
        state.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref initCalls))
            .Returns(initGate.Task);

        state.Setup(s => s.TryGetPeerSnapshot(It.IsAny<Guid>())).Returns((SimulatedPeerSnapshot?)null);
        state.Setup(s => s.SnapshotPeers()).Returns(Array.Empty<SimulatedPeerSnapshot>());

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
        sut.Peers[0].PeerId.Should().Be(peerId);
        sut.Peers[0].DisplayName.CurrentValue.Should().Be("Alice");
        sut.Peers[0].IsRelayCapable.CurrentValue.Should().BeTrue();
    }
}
