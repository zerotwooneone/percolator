using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.MessageQueue.Abstractions;
using Percolator.Network;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Network;

/// <summary>
/// Orchestrates one-by-one relay of queued opaque messages to a peer and awaits an RPC-level acknowledgment.
/// </summary>
public class RelayOrchestrator
{
    private readonly ILogger<RelayOrchestrator> _logger;
    private readonly IMessageQueueRepository _queue;
    private readonly IDirectSessionRepository _directSessions;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _active;

    public RelayOrchestrator(
        ILogger<RelayOrchestrator> logger,
        IMessageQueueRepository queue,
        IDirectSessionRepository directSessions,
        ISecureMessagingService secureMessaging,
        IMessageTransportService transport,
        ActiveIdentityContext active)
    {
        _logger = logger;
        _queue = queue;
        _directSessions = directSessions;
        _secureMessaging = secureMessaging;
        _transport = transport;
        _active = active;
    }

    /// <summary>
    /// Attempts to relay the next queued message for the given peer. Stops on first failure.
    /// Returns true if a message was relayed and acked; false if no messages were available.
    /// Throws on transport or decryption failures to stop the outer loop.
    /// </summary>
    public async Task<bool> RelayNextAsync(IdentityPeerId recipientPeerId, CancellationToken ct = default)
    {
        if (_active.Identity is null)
        {
            throw new InvalidOperationException("Active identity not initialized");
        }
        var selfIdentityId = _active.Identity.SelfIdentityId;
        // Fetch one queued item (AckId, Blob)
        var items = await _queue.FetchAsync(recipientPeerId, 1, ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return false;
        }

        var (ackId, blob) = items[0];

        // Resolve a direct session to the peer
        var session = await _directSessions.GetByRemotePeerIdAsync(new NetworkPeerId(recipientPeerId.Value), selfIdentityId.Value).ConfigureAwait(false);
        if (session is null)
        {
            throw new InvalidOperationException($"No direct session for peer {recipientPeerId} to relay message {ackId}");
        }
        var sessionId = new SessionId(session.SessionId.Value);
        var directSessionId = new DirectSessionId(session.SessionId.Value);

        // Build RelayOpaqueEnvelope with AckId
        var relay = new RelayOpaqueEnvelope
        {
            Version = 1,
            OpaquePayload = ByteString.CopyFrom(blob),
            MessageAckId = ByteString.CopyFrom(ackId.ToByteArray())
        };
        var env = new InternalEnvelope { RelayOpaqueEnvelope = relay };

        // Encrypt and send
        var plaintext = new Plaintext(env.ToByteArray());
        var cipher = await _secureMessaging.EncryptAsync(sessionId, plaintext, ct).ConfigureAwait(false);
        var response = await _transport.SendMessageAsync(recipientPeerId, directSessionId, cipher, ct).ConfigureAwait(false);

        // Expect RPC-level response payload (DR-ciphertext)
        if (response.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload ||
            response.ResponsePayload == null || !response.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("Relay deliver returned no response payload to decode ack");
        }

        // Decrypt response payload as RelayOpaqueResponse
        var ackCipher = new SessionRatchetMessage(response.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(selfIdentityId.Value, ackCipher, ct).ConfigureAwait(false);
        var ackPlain = resolved?.plaintext;
        if (ackPlain is null)
        {
            throw new InvalidOperationException("Failed to decrypt RelayOpaqueResponse");
        }
        var ack = RelayOpaqueResponse.Parser.ParseFrom(ackPlain.Value);
        if (!ack.HasMessageAckId)
        {
            throw new InvalidOperationException("RelayOpaqueResponse missing message_ack_id");
        }

        var returnedAck = new Guid(ack.MessageAckId.ToByteArray());
        if (returnedAck != ackId)
        {
            _logger.LogWarning("RelayOpaqueResponse ack id mismatch. expected {Expected} got {Actual}", ackId, returnedAck);
            throw new InvalidOperationException("AckId mismatch in RelayOpaqueResponse");
        }

        // Idempotent delete by AckId
        await _queue.DeleteByAckIdAsync(ackId, ct).ConfigureAwait(false);
        return true;
    }
}
