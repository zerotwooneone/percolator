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
        state.Setup(s => s.PublishPreKeyBundleAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        state.Setup(s => s.TryPopPreKeyBundleByRecipientPkhAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublishedPreKeyBundleDto?)null);

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, diagnostics, pending);

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

    [Test]
    public async Task When_initiating_standard_handshake_via_relay_it_enqueues_handshake_initiator_hello_to_relay_host()
    {
        // Arrange
        var simulatedPeerId = Guid.NewGuid();
        var relayHostPeerId = Guid.NewGuid();

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        var initiatorIdentitySpki = initiatorIdentityEcdh.PublicKey.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(simulatedPeerId, "sim", isOnline: true, isRelayCapable: false, initiatorIdentitySpki, initiatorIdentityPriv);
        using var directory = new DirectoryStub(model);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(Percolator.Application.Network.PercolatorMessageService));

        var pending = new SimulatedPeerPendingInbox();

        var bundle = CreateValidResponderPreKeyBundle();
        var responderPkh = bundle.ResponderPkh;
        var preKeyBundleBytes = bundle.BundleBytes;

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers)
            .Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(new ObservableCollection<SimulatedPeerDto>()));
        state.Setup(s => s.TryGetRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);
        state.Setup(s => s.TryPopPreKeyBundleByRecipientPkhAsync(relayHostPeerId, responderPkh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishedPreKeyBundleDto
            {
                RecipientPublicKeyHash = responderPkh,
                LogicalOwnerPeerId = Guid.NewGuid(),
                BundleBytes = preKeyBundleBytes,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });
        state.Setup(s => s.EnqueueRelayOpaqueAsync(relayHostPeerId, responderPkh, It.IsAny<byte[]>(), nameof(HandshakeInitiatorHello), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        state.Setup(s => s.SaveRuntimeStoreAsync(simulatedPeerId, It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, diagnostics, pending);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            responderPkh,
            CancellationToken.None);

        // Assert
        sid.Should().BeNull();
        state.Verify(s => s.EnqueueRelayOpaqueAsync(relayHostPeerId, responderPkh, It.IsAny<byte[]>(), nameof(HandshakeInitiatorHello), It.IsAny<CancellationToken>()), Times.Once);
        state.Verify(s => s.SaveRuntimeStoreAsync(simulatedPeerId, It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()), Times.Once);

        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.PreKeyBundleFetched
            && e.RelayHostPeerId == relayHostPeerId);
        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued
            && e.PeerId == simulatedPeerId
            && e.RelayHostPeerId == relayHostPeerId);
    }

    [Test]
    public async Task When_relay_returns_bundle_with_mismatched_identity_key_it_does_not_enqueue_handshake_hello()
    {
        // Arrange
        var simulatedPeerId = Guid.NewGuid();
        var relayHostPeerId = Guid.NewGuid();

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        var initiatorIdentitySpki = initiatorIdentityEcdh.PublicKey.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(simulatedPeerId, "sim", isOnline: true, isRelayCapable: false, initiatorIdentitySpki, initiatorIdentityPriv);
        using var directory = new DirectoryStub(model);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(Percolator.Application.Network.PercolatorMessageService));

        var pending = new SimulatedPeerPendingInbox();

        var requestedResponderPkh = SHA256.HashData(Guid.NewGuid().ToByteArray());

        // Bundle is valid, but its identity key hashes to a different PKH.
        var bundleBytes = CreateValidResponderPreKeyBundle().BundleBytes;

        var state = new Mock<ISimulatorStateService>(MockBehavior.Strict);
        state.SetupGet(s => s.Peers)
            .Returns(new ReadOnlyObservableCollection<SimulatedPeerDto>(new ObservableCollection<SimulatedPeerDto>()));
        state.Setup(s => s.TryGetRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);
        state.Setup(s => s.TryPopPreKeyBundleByRecipientPkhAsync(relayHostPeerId, requestedResponderPkh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishedPreKeyBundleDto
            {
                RecipientPublicKeyHash = requestedResponderPkh,
                LogicalOwnerPeerId = Guid.NewGuid(),
                BundleBytes = bundleBytes,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            });

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, diagnostics, pending);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            requestedResponderPkh,
            CancellationToken.None);

        // Assert
        sid.Should().BeNull();
        state.Verify(s => s.EnqueueRelayOpaqueAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        state.Verify(s => s.SaveRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()), Times.Never);

        diagnostics.Events.Should().NotContain(e => e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued);
    }

    private sealed record ResponderBundleFixture(byte[] BundleBytes, byte[] ResponderPkh);

    private static ResponderBundleFixture CreateValidResponderPreKeyBundle()
    {
        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderSignedPreKeySpki = responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var preKeySig = responderIdentityEcdsa.SignData(responderSignedPreKeySpki, HashAlgorithmName.SHA256);

        var bundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(responderIdentitySpki),
            SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(responderSignedPreKeySpki),
            PreKeySignature = ByteString.CopyFrom(preKeySig)
        };

        var bytes = bundle.ToByteArray();
        var pkh = SHA256.HashData(responderIdentitySpki);
        return new ResponderBundleFixture(bytes, pkh);
    }
}
