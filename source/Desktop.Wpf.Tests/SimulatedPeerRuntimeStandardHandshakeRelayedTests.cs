using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Google.Protobuf;
using Moq;
using NUnit.Framework;
using Percolator.Contracts;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeStandardHandshakeRelayedTests
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
    public async Task Relayed_HandshakeInitiatorHello_is_handled_and_persists_runtime_store()
    {
        var simulatedPeerId = Guid.NewGuid();

        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderIdentityPriv = responderIdentityEcdh.ExportECPrivateKey();
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(simulatedPeerId, "sim", isOnline: true, isRelayCapable: false, responderIdentitySpki, responderIdentityPriv);
        using var directory = new DirectoryStub(model);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(Percolator.Application.Network.PercolatorMessageService));

        var pending = new SimulatedPeerPendingInbox();

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers)
            .Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(new ObservableCollection<SimulatedPeerDto>()));
        state.Setup(s => s.TryGetRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);
        state.Setup(s => s.SaveRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();
        state.Setup(s => s.EnqueueRelayOpaqueAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, pending);

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var initiatorEph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorEphSpki = initiatorEph.PublicKey.ExportSubjectPublicKeyInfo();

        var spkId = Guid.NewGuid();
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorIdentitySpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(initiatorEphSpki),
            SignedPreKeyId = ByteString.CopyFrom(spkId.ToByteArray())
        };

        var resp = await sut.ReceiveRelayedOpaquePayloadAsync(simulatedPeerId, hello.ToByteArray(), CancellationToken.None);

        resp.Should().NotBeNull();
        resp!.Version.Should().Be(1);
        resp.Response.Should().NotBeNull();
        resp.Response.ResponsePayload.Should().NotBeNull();

        state.Verify(s => s.SaveRuntimeStoreAsync(simulatedPeerId, It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
