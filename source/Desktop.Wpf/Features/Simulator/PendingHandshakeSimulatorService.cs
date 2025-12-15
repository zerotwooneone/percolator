using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Contracts;
using Percolator.Application.Ingress;
using Percolator.Cryptography;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Desktop.Wpf.Features.Simulator;

public interface IPendingHandshakeSimulatorService
{
    Task<PendingHandshakeIngressResult> AddSyntheticPendingAsync(string? displayName = null, byte[]? invitationPayload = null, CancellationToken ct = default);
}

public sealed class PendingHandshakeSimulatorService : IPendingHandshakeSimulatorService
{
    private readonly IPendingHandshakeIngress _pendingIngress;

    public PendingHandshakeSimulatorService(
        IPendingHandshakeIngress pendingIngress)
    {
        _pendingIngress = pendingIngress ?? throw new ArgumentNullException(nameof(pendingIngress));
    }

    public async Task<PendingHandshakeIngressResult> AddSyntheticPendingAsync(string? displayName = null, byte[]? invitationPayload = null, CancellationToken ct = default)
    {
        // Build a realistic HandshakeInitiatorHello per request (fresh keys)
        // Identity key: ECDSA P-256 (SPKI)
        byte[] identitySpki;
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            identitySpki = ecdsa.ExportSubjectPublicKeyInfo();
        }

        // Ephemeral ratchet key: ECDH P-256 (SPKI)
        byte[] ephSpki;
        using (var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            ephSpki = ecdh.ExportSubjectPublicKeyInfo();
        }

        var hello = new HandshakeInitiatorHello
        {
            InitiatorIdentityKeySpki = ByteString.CopyFrom(identitySpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(ephSpki),
            SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray())
        };
        if (invitationPayload is not null && invitationPayload.Length > 0)
        {
            hello.EncryptedPayload = ByteString.CopyFrom(invitationPayload);
        }

        var helloBytes = hello.ToByteArray();
        return await _pendingIngress.CreateFromInitiatorHelloAsync(helloBytes, displayName, ttl: null, ct).ConfigureAwait(false);
    }
}
