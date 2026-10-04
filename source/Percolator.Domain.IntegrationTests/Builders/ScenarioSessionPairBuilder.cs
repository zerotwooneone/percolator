using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests.Builders;

public sealed class ScenarioSessionPairBuilder
{
    private readonly ScenarioParticipant _initiator;
    private readonly ScenarioParticipant _responder;
    private readonly ScenarioCryptoEngine _engine;

    private ScenarioSessionPairBuilder(ScenarioParticipant initiator, ScenarioParticipant responder, ScenarioCryptoEngine engine)
    {
        _initiator = initiator;
        _responder = responder;
        _engine = engine;
    }

    public static ScenarioSessionPairBuilder Between(ScenarioParticipant initiator, ScenarioParticipant responder, ScenarioCryptoEngine engine)
        => new(initiator, responder, engine);

    public (DirectRatchetSession InitiatorSession, DirectRatchetSession ResponderSession) ViaDirectX3dh()
    {
        var responderBundle = _responder.CreatePreKeyBundle();
        var x3dhInit = X3dhAgreement.Initiate(
            _initiator.IdentityPrivateKey.Span,
            _initiator.IdentityPublicKey,
            responderBundle,
            _engine).Value!;

        var initSession = DirectRatchetSession.CreateFromX3dhInitiator(
            _initiator.IdentityId,
            _initiator.DeviceId,
            _responder.IdentityId,
            _responder.DeviceId,
            x3dhInit,
            _responder.SignedPreKeyPublic,
            _engine).Value!;

        var responderMasterSecret = X3dhAgreement.Receive(
            _responder.IdentityPrivateKey.Span,
            _responder.SignedPreKeyPrivate.Span,
            receiverOneTimePreKeyPrivateKeyOrEmpty: default,
            _initiator.IdentityPublicKey,
            x3dhInit.EphemeralPublicKey,
            _engine).Value!;

        var respSession = DirectRatchetSession.CreateFromX3dhResponder(
            _responder.IdentityId,
            _responder.DeviceId,
            _initiator.IdentityId,
            _initiator.DeviceId,
            responderMasterSecret,
            x3dhInit.EphemeralPublicKey,
            _engine).Value!;

        return (initSession, respSession);
    }

    public async Task<(DirectRatchetSession InitiatorSession, DirectRatchetSession ResponderSession)> ViaRelayedOpkAsync(
        RelayHostedPreKeyBundle hostedBundle)
    {
        var consumedBundle = hostedBundle.ConsumeBundle().Value!;
        uint opkId = consumedBundle.OneTimePreKeyId;

        var x3dhInit = X3dhAgreement.Initiate(
            _initiator.IdentityPrivateKey.Span,
            _initiator.IdentityPublicKey,
            consumedBundle,
            _engine).Value!;

        var initSession = DirectRatchetSession.CreateFromX3dhInitiator(
            _initiator.IdentityId,
            _initiator.DeviceId,
            _responder.IdentityId,
            _responder.DeviceId,
            x3dhInit,
            _responder.SignedPreKeyPublic,
            _engine).Value!;

        var consumedOpkPriv = await _responder.PreKeyStore.TryConsumeOneTimePreKeyPrivateAsync(
            _responder.IdentityId, _responder.DeviceId, opkId);

        var responderMasterSecret = X3dhAgreement.Receive(
            _responder.IdentityPrivateKey.Span,
            _responder.SignedPreKeyPrivate.Span,
            consumedOpkPriv!.Span,
            _initiator.IdentityPublicKey,
            x3dhInit.EphemeralPublicKey,
            _engine).Value!;

        var respSession = DirectRatchetSession.CreateFromX3dhResponder(
            _responder.IdentityId,
            _responder.DeviceId,
            _initiator.IdentityId,
            _initiator.DeviceId,
            responderMasterSecret,
            x3dhInit.EphemeralPublicKey,
            _engine).Value!;

        return (initSession, respSession);
    }
}
