using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Network;
using ISigningService = Percolator.Network.ISigningService;

namespace Percolator.Application.Network;

public interface IMainReverseSignalInviteFactory
{
    EstablishDirectSessionRequest CreateInvite();
}

public sealed class MainReverseSignalInviteFactory : IMainReverseSignalInviteFactory
{
    private readonly ActiveIdentityContext _active;
    private readonly ISigningService _signing;
    private readonly IOptions<TransportOptions> _transportOptions;
    private readonly IAdvertisedHostLookup _advertisedHostLookup;
    private readonly ISelfPreKeyBundleRepository _selfPreKeys;
    private readonly ISentInvitationRepository _sentInvitations;
    private readonly IClock _clock;

    public MainReverseSignalInviteFactory(
        ActiveIdentityContext active,
        ISigningService signing,
        IOptions<TransportOptions> transportOptions,
        IAdvertisedHostLookup advertisedHostLookup,
        ISelfPreKeyBundleRepository selfPreKeys,
        ISentInvitationRepository sentInvitations,
        IClock clock)
    {
        _active = active;
        _signing = signing;
        _transportOptions = transportOptions;
        _advertisedHostLookup = advertisedHostLookup;
        _selfPreKeys = selfPreKeys;
        _sentInvitations = sentInvitations;
        _clock = clock;
    }

    public EstablishDirectSessionRequest CreateInvite()
    {
        if (_active.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }

        if (_active.Keys?.IdentitySigningKey is null)
        {
            throw new InvalidOperationException("Active identity keys not loaded.");
        }

        var correlation = Guid.NewGuid();
        var port = _transportOptions.Value.GrpcPort;
        if (port == 0) port = 5001;

        var inviterHost = _advertisedHostLookup.GetAdvertisedHostAsync().GetAwaiter().GetResult();

        var expiresAtUtc = _clock.UtcNow.AddMinutes(10);

        // IMPORTANT: For reverse-signal, the inviter must remember which signed pre-key private material
        // corresponds to the public signed pre-key included in the invite. We generate a fresh SPK per invite
        // and persist+track it by request_correlation_id.
        var signedPreKeyId = Guid.NewGuid();
        using var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeySpki = spk.ExportSubjectPublicKeyInfo();
        var signedPreKeyPriv = spk.ExportECPrivateKey();

        var preKeySig = _signing.Sign(new Payload(signedPreKeySpki));

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = inviterHost,
            InviterPort = (uint)port,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(expiresAtUtc),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(signedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig.Value)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = _signing.Sign(new Payload(payloadBytes));

        var inviterIdentityKeySpki = _signing.GetActivePublicKey().Value;

        // Persist signed pre-key and track correlation for finalization.
        _selfPreKeys.SaveSignedPreKeyAsync(
                _active.Identity.SelfIdentityId.Value,
                signedPreKeyId,
                signedPreKeyPriv,
                signedPreKeySpki,
                preKeySig.Value,
                expiresAtUtc)
            .GetAwaiter()
            .GetResult();

        _sentInvitations.UpsertAsync(new SentInvitation(
                new RequestCorrelationId(correlation),
                signedPreKeyId,
                oneTimePreKeyId: null,
                targetPeerId: null,
                createdAtUtc: _clock.UtcNow,
                expiresAtUtc: expiresAtUtc))
            .GetAwaiter()
            .GetResult();

        return new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentityKeySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig.Value)
        };
    }
}
