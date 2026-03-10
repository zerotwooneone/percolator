using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Moq;
using NUnit.Framework;
using Percolator.Contracts;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeTests
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
    public async Task Accept_finalize_creates_session_when_signed_prekey_private_is_recorded()
    {
        var pending = new SimulatedPeerPendingInbox();

        var inviterPeerId = Guid.NewGuid();
        var acceptorPeerId = Guid.NewGuid();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterIdentityPriv = inviterIdentityEcdh.ExportECPrivateKey();
        using var inviterIdentityEcdsa = ECDsa.Create(inviterIdentityEcdh.ExportParameters(true));
        var inviterIdentitySpki = inviterIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var inviterModel = new SimulatedPeerModel(inviterPeerId, "inviter", isOnline: true, isRelayCapable: false, inviterIdentitySpki, inviterIdentityPriv);
        var acceptorModel = new SimulatedPeerModel(acceptorPeerId, "acceptor", isOnline: true, isRelayCapable: false, acceptorIdentitySpki, acceptorIdentityPriv);

        using var directory = new DirectoryStub(inviterModel, acceptorModel);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(Percolator.Application.Network.PercolatorMessageService));
        var state = new Mock<ISimulatorStateService>(MockBehavior.Loose);
        state.Setup(s => s.TryGetRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);
        state.Setup(s => s.SaveRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, diagnostics, pending);

        var correlation = Guid.NewGuid();

        using var inviterSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var inviterSignedPreKeyPriv = inviterSignedPreKey.ExportECPrivateKey();
        var preKeySig = inviterIdentityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        sut.RecordOutboundInviteSignedPreKeyPrivate(inviterPeerId, correlation, inviterSignedPreKeyPriv);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.0.0.1",
            InviterPort = 5002,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = inviterIdentityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var invite = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };

        var acceptance = await sut.AcceptReverseSignalInviteAsync(
            simulatedPeerId: acceptorPeerId,
            inviterPeerId: inviterPeerId,
            invite: invite,
            cancellationToken: CancellationToken.None);

        pending.AddInviteHandshakeResponse(inviterPeerId, correlation, acceptance.Response);

        var finalizedSid = await sut.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        finalizedSid.Should().NotBeNull();

        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }

    [Test]
    public async Task Accept_finalize_returns_null_when_missing_signed_prekey_private()
    {
        var pending = new SimulatedPeerPendingInbox();

        var inviterPeerId = Guid.NewGuid();
        var acceptorPeerId = Guid.NewGuid();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterIdentityPriv = inviterIdentityEcdh.ExportECPrivateKey();
        using var inviterIdentityEcdsa = ECDsa.Create(inviterIdentityEcdh.ExportParameters(true));
        var inviterIdentitySpki = inviterIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var inviterModel = new SimulatedPeerModel(inviterPeerId, "inviter", isOnline: true, isRelayCapable: false, inviterIdentitySpki, inviterIdentityPriv);
        var acceptorModel = new SimulatedPeerModel(acceptorPeerId, "acceptor", isOnline: true, isRelayCapable: false, acceptorIdentitySpki, acceptorIdentityPriv);

        using var directory = new DirectoryStub(inviterModel, acceptorModel);

        var state = new Mock<ISimulatorStateService>(MockBehavior.Loose);
        state.Setup(s => s.TryGetRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SimulatedPeerRuntimeStoreDto?)null);
        state.Setup(s => s.SaveRuntimeStoreAsync(It.IsAny<Guid>(), It.IsAny<SimulatedPeerRuntimeStoreDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var messageService = (Percolator.Application.Network.PercolatorMessageService)
            FormatterServices.GetUninitializedObject(typeof(Percolator.Application.Network.PercolatorMessageService));

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatedPeerRuntimeService(directory, messageService, state.Object, diagnostics, pending);

        var correlation = Guid.NewGuid();

        pending.AddInviteHandshakeResponse(inviterPeerId, correlation, new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = correlation.ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(acceptorIdentitySpki),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x02 })
        });

        var finalizedSid = await sut.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        finalizedSid.Should().BeNull();
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeTrue();
    }
}
