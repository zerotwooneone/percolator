using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity.Model;
using Percolator.Network;
using ISigningService = Percolator.Network.ISigningService;

namespace Percolator.Application.Network;

public interface IMainReverseSignalInviteFactory
{
    EstablishDirectSessionRequest CreateInvite();

    EstablishDirectSessionRequest CreateInvite(
        string? targetDisplayName,
        string? targetEndpointHost,
        int? targetEndpointPort,
        ListeningPort listeningPort);
}

public sealed class MainReverseSignalInviteFactory : IMainReverseSignalInviteFactory
{
    private readonly ActiveIdentityContext _active;
    private readonly ISigningService _signing;
    private readonly IAdvertisedHostLookup _advertisedHostLookup;
    private readonly ISelfPreKeyBundleRepository _selfPreKeys;
    private readonly ISentInvitationRepository _sentInvitations;
    private readonly IClock _clock;
    private readonly MediatR.IMediator _mediator;

    public MainReverseSignalInviteFactory(
        ActiveIdentityContext active,
        ISigningService signing,
        IAdvertisedHostLookup advertisedHostLookup,
        ISelfPreKeyBundleRepository selfPreKeys,
        ISentInvitationRepository sentInvitations,
        IClock clock,
        MediatR.IMediator mediator)
    {
        _active = active;
        _signing = signing;
        _advertisedHostLookup = advertisedHostLookup;
        _selfPreKeys = selfPreKeys;
        _sentInvitations = sentInvitations;
        _clock = clock;
        _mediator = mediator;
    }

    public EstablishDirectSessionRequest CreateInvite()
        => CreateInvite(targetDisplayName: null, targetEndpointHost: null, targetEndpointPort: null, listeningPort: _active.Identity?.ListeningPort ?? throw new InvalidOperationException("Active identity not loaded."));

    public EstablishDirectSessionRequest CreateInvite(
        string? targetDisplayName,
        string? targetEndpointHost,
        int? targetEndpointPort,
        ListeningPort listeningPort)
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

        var inviterHost = _advertisedHostLookup.GetAdvertisedHostAsync().GetAwaiter().GetResult();

        var expiresAtUtc = _clock.UtcNow.AddDays(10);

        // IMPORTANT: For reverse-signal, the inviter must remember which signed pre-key private material
        // corresponds to the public signed pre-key included in the invite. We generate a fresh SPK per invite
        // and persist+track it by request_correlation_id.
        var signedPreKeyId = Guid.NewGuid();
        using var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeySpki = spk.ExportSubjectPublicKeyInfo();
        var signedPreKeyPriv = spk.ExportECPrivateKey();

        var preKeySig = _signing.Sign(Payload.FromBytes(signedPreKeySpki));

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = inviterHost,
            InviterPort = (uint)listeningPort.Value,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(expiresAtUtc),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(signedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig.ToArray())
            },
            InviterPublicIdentityId = ByteString.CopyFrom(_active.Identity.PublicIdentityId.Value.ToByteArray())
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = _signing.Sign(Payload.FromBytes(payloadBytes));

        var inviterIdentityKeySpki = _signing.GetActivePublicKey().ToArray();

        // Persist signed pre-key and track correlation for finalization.
        _selfPreKeys.SaveSignedPreKeyAsync(
                _active.Identity.SelfIdentityId,
                signedPreKeyId,
                signedPreKeyPriv,
                signedPreKeySpki,
                preKeySig.ToArray(),
                expiresAtUtc)
            .GetAwaiter()
            .GetResult();

        _sentInvitations.UpsertAsync(new SentInvitation(
                new RequestCorrelationId(correlation),
                signedPreKeyId,
                oneTimePreKeyId: null,
                targetPeerId: null,
                createdAtUtc: _clock.UtcNow,
                expiresAtUtc: expiresAtUtc,
                targetDisplayName: targetDisplayName,
                targetEndpointHost: targetEndpointHost,
                targetEndpointPort: targetEndpointPort),
            new CryptoSelfId(_active.Identity.SelfIdentityId.Value))
            .GetAwaiter()
            .GetResult();

        _mediator
            .Publish(new SentInvitationUpsertedNotification(new RequestCorrelationId(correlation)))
            .GetAwaiter()
            .GetResult();

        return new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentityKeySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig.ToArray())
        };
    }
}
