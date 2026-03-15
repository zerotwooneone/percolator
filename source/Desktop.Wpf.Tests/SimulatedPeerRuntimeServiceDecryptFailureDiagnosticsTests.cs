using System;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeServiceDecryptFailureDiagnosticsTests
{
    private sealed class DirectoryStub : ISimulatedPeerDirectory
    {
        private readonly ObservableCollection<SimulatedPeerModel> _peers;

        public DirectoryStub(params SimulatedPeerModel[] peers)
        {
            _peers = new ObservableCollection<SimulatedPeerModel>(peers);
            Peers = new ReadOnlyObservableCollection<SimulatedPeerModel>(_peers);
        }

        public ReadOnlyObservableCollection<SimulatedPeerModel> Peers { get; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SimulatedPeerModel> AddPeerAsync(string? displayName, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task RemovePeerAsync(Guid peerId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public void Dispose()
        {
            foreach (var p in _peers)
            {
                p.Dispose();
            }
        }
    }

    [Test]
    public async Task DecryptSessionMessageAsync_WhenDecryptThrows_EmitsDecryptFailureDiagnosticEvent()
    {
        // Arrange
        var peerId = Guid.NewGuid();
        using var identityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var identityPriv = identityEcdh.ExportECPrivateKey();
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));
        var identitySpki = identityEcdsa.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(peerId, "peer", isOnline: true, isRelayCapable: false, identitySpki, identityPriv);
        using var directory = new DirectoryStub(model);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(Percolator.Application.Network.PercolatorMessageService));

        var state = new Mock<ISimulatorStateService>(MockBehavior.Loose);
        state.Setup(s => s.TryGetRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);
        state.Setup(s => s.SaveRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, diagnostics, pending);

        var sessionId = new SessionId(Guid.NewGuid());
        var badMessage = new SessionRatchetMessage(RandomNumberGenerator.GetBytes(10));

        // Act
        var act = async () => await sut.DecryptSessionMessageAsync(peerId, sessionId, badMessage, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.DecryptFailure
            && e.PeerId == peerId
            && e.Message.Contains("Decrypt failure", StringComparison.Ordinal));
    }
}
