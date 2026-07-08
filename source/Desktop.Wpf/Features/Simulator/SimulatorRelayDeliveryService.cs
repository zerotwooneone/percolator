using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorRelayDeliveryService
{
    Task DeliverToPeerAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Network.NetworkPeerId recipientNetworkPeerId,
        Guid ackId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken = default);
}

public sealed class SimulatorRelayDeliveryService : ISimulatorRelayDeliveryService
{
    private readonly ISimulatorStateService _state;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ISelfIdentityKeysStore _selfIdentityKeysStore;
    private readonly ILogger<SimulatorRelayDeliveryService> _logger;
    private readonly ISimulatorDiagnosticsService _diagnostics;

    public SimulatorRelayDeliveryService(
        ISimulatorStateService state,
        ISelfIdentityRepository selfIdentityRepository,
        ISelfIdentityKeysStore selfIdentityKeysStore,
        ILogger<SimulatorRelayDeliveryService> logger,
        ISimulatorDiagnosticsService diagnostics)
    {
        _state = state;
        _selfIdentityRepository = selfIdentityRepository;
        _selfIdentityKeysStore = selfIdentityKeysStore;
        _logger = logger;
        _diagnostics = diagnostics;
    }
    
    public async Task DeliverToPeerAsync(
        Percolator.Network.NetworkPeerId relayHostNetworkPeerId,
        Percolator.Network.NetworkPeerId recipientNetworkPeerId,
        Guid ackId,
        byte[] opaqueBytes,
        string? debugType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (opaqueBytes is null) throw new ArgumentNullException(nameof(opaqueBytes));

        try
        {
            var hello = HandshakeInitiatorHello.Parser.ParseFrom(opaqueBytes);
            if (hello is not null
                && hello.HasInitiatorIdentityKeySpki && hello.InitiatorIdentityKeySpki.Length > 0
                && hello.HasInitiatorEphemeralKeySpki && hello.InitiatorEphemeralKeySpki.Length > 0
                && hello.HasSignedPreKeyId && hello.SignedPreKeyId.Length > 0)
            {
                await _state.UpsertPendingStandardSignalHelloAsync(
                        recipientNetworkPeerId: recipientNetworkPeerId,
                        relayHostNetworkPeerId: relayHostNetworkPeerId,
                        hello: hello,
                        receivedUtc: DateTimeOffset.UtcNow,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }
        catch
        {
            // Not a hello; fall through.
        }

        var forwarded = await _state
            .ReceiveRelayedOpaquePayloadAsync(recipientNetworkPeerId, opaqueBytes, cancellationToken)
            .ConfigureAwait(false);

        if (forwarded?.Response is null || !forwarded.Response.HasResponsePayload || forwarded.Response.ResponsePayload.Length == 0)
        {
            return;
        }

        // Route response back to initiator PKH.
        try
        {
            var hello = HandshakeInitiatorHello.Parser.ParseFrom(opaqueBytes);
            if (hello is null || !hello.HasInitiatorIdentityKeySpki || hello.InitiatorIdentityKeySpki.Length == 0)
            {
                return;
            }

            var initiatorPkh = Percolator.Identity.IdentityPublicKeyHash.FromSpki(hello.InitiatorIdentityKeySpki.ToByteArray());

            var initiatorPeerId = await _state
                .TryGetPeerIdByIdentityPublicKeyHashAsync(initiatorPkh, cancellationToken)
                .ConfigureAwait(false);

            if (initiatorPeerId is not null)
            {
                await _state.EnqueueRelayDownstreamToPeerAsync(
                        relayHostNetworkPeerId: relayHostNetworkPeerId,
                        targetIdentityPublicKeyHash: initiatorPkh,
                        opaqueBytes: forwarded.ToByteArray(),
                        debugType: nameof(EstablishSessionResponse),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var matchesMainIdentity = await MatchesAnyMainIdentityPkhAsync(initiatorPkh, cancellationToken).ConfigureAwait(false);
                if (matchesMainIdentity)
                {
                    await _state.EnqueueRelayUpstreamToMainAsync(
                            relayHostNetworkPeerId: relayHostNetworkPeerId,
                            opaqueBytes: forwarded.ToByteArray(),
                            debugType: nameof(EstablishSessionResponse),
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    _diagnostics.Emit(
                        SimulatorDiagnosticEventType.RelayRoutingFailure,
                        $"Relay response routing failure (no simulated peer or main identity for PKH): {nameof(EstablishSessionResponse)}",
                        relayHostPeerId: relayHostNetworkPeerId,
                        ackId: ackId);
                    return;
                }
            }

            _diagnostics.Emit(
                SimulatorDiagnosticEventType.HandshakeStateTransition,
                "Standard handshake response enqueued (relayed)",
                peerId: recipientNetworkPeerId,
                relayHostPeerId: relayHostNetworkPeerId,
                contextTag: "ResponseEnqueued");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[simulator] Failed to enqueue EstablishSessionResponse back to relay host {RelayHost}", relayHostNetworkPeerId);
        }
    }

    private async Task<bool> MatchesAnyMainIdentityPkhAsync(IdentityPublicKeyHash pkh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pkh is null) throw new ArgumentNullException(nameof(pkh));

        IReadOnlyList<Percolator.Identity.Model.SelfIdentity> identities;
        try
        {
            identities = await _selfIdentityRepository.ListAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();

            X3dhKeys? keys;
            try
            {
                keys = await _selfIdentityKeysStore.LoadAsync(identity.Id, ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            if (keys is null)
            {
                continue;
            }

            try
            {
                var spki = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
                var computed = IdentityPublicKeyHash.FromSpki(spki);
                if (computed.Equals(pkh))
                {
                    return true;
                }
            }
            finally
            {
                try { keys.Dispose(); } catch { }
            }
        }

        return false;
    }
    
}
