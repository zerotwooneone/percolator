using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using Percolator.Cryptography;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Network;
namespace Percolator.Application.Sessions;

/// <summary>
/// Provides messaging services for the application, working directly with cryptography primitives.
/// </summary>
    public class MessageService : IMessageService
    {
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transportService;
    private readonly ILogger<MessageService> _logger;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public MessageService(
        IDirectSessionManager sessionManager,
        IMessageTransportService transportService,
        ILogger<MessageService> logger,
        ActiveIdentityContext activeIdentityContext)
    {
        _sessionManager = sessionManager;
        _transportService = transportService;
        _logger = logger;
        _activeIdentityContext = activeIdentityContext;
        
    }

    public async Task SendDirectMessageAsync(
        DirectSessionId directSessionId,
        InternalEnvelope envelope,
        IdentityPeerId remotePeerId,
        CancellationToken cancellationToken = default)
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Identity context not loaded");
        }
        var sessionId = new SessionId(directSessionId.Value);
        var plaintext = new Plaintext(envelope.ToByteArray());
        var encrypted = await _sessionManager.EncryptMessageAsync(sessionId, plaintext).ConfigureAwait(false);
        //todo: lookup peer connection and attempt relay if direct fails
        await _transportService.SendMessageAsync(remotePeerId, directSessionId, encrypted, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Percolator.Contracts.DeliverOpaqueMessageResponse> SendDirectEnvelopeWithResponseAsync(
        DirectSessionId directSessionId,
        InternalEnvelope envelope,
        IdentityPeerId remotePeerId,
        CancellationToken cancellationToken = default)
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Identity context not loaded");
        }
        var sessionId = new SessionId(directSessionId.Value);
        var plaintext = new Plaintext(envelope.ToByteArray());
        var encrypted = await _sessionManager.EncryptMessageAsync(sessionId, plaintext).ConfigureAwait(false);
        //todo: lookup peer connection and attempt relay if direct fails
        var response = await _transportService.SendMessageAsync(remotePeerId, directSessionId, encrypted, cancellationToken).ConfigureAwait(false);
        return response;
    }
}
