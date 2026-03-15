using Microsoft.Extensions.Logging;
using MediatR;

namespace Percolator.Application.Apps.Chat
{
    // Red phase stub: command + handler signature only
    public sealed record CreateGroupConversationCommand(
        int SelfIdentityId,
        Guid GroupConversationGuid,
        IReadOnlyList<byte[]> ParticipantIdentityKeysSpki,
        string? Name,
        byte[] CreatorIdentityKeySpki
    ) : IRequest;

    internal sealed class CreateGroupConversationHandler : IRequestHandler<CreateGroupConversationCommand>
    {
        private readonly ILogger<CreateGroupConversationHandler> _logger;
        private readonly Percolator.Identity.IPeerPublicSigningKeyStore _keyStore;
        private readonly Percolator.Chat.IConversationRepository _conversations;
        private readonly Percolator.Chat.App.IGroupAdminKeyStore _adminKeys;

        public CreateGroupConversationHandler(
            ILogger<CreateGroupConversationHandler> logger,
            Percolator.Identity.IPeerPublicSigningKeyStore keyStore,
            Percolator.Chat.IConversationRepository conversations,
            Percolator.Chat.App.IGroupAdminKeyStore adminKeys)
        {
            _logger = logger;
            _keyStore = keyStore;
            _conversations = conversations;
            _adminKeys = adminKeys;
        }

        public Task Handle(CreateGroupConversationCommand request, CancellationToken cancellationToken)
        {
            return HandleInternalAsync(request, cancellationToken);
        }

        private async Task HandleInternalAsync(CreateGroupConversationCommand request, CancellationToken cancellationToken)
        {
            if (request.ParticipantIdentityKeysSpki is null || request.ParticipantIdentityKeysSpki.Count == 0)
                throw new InvalidOperationException("At least one participant identity key is required.");
            if (request.CreatorIdentityKeySpki is null || request.CreatorIdentityKeySpki.Length == 0)
                throw new InvalidOperationException("Creator identity key is required.");

            // Resolve SPKIs -> PeerIds
            var participants = new List<Percolator.Chat.ValueObjects.ParticipantId>();
            int index = 0;
            foreach (var spki in request.ParticipantIdentityKeysSpki)
            {
                if (spki == null || spki.Length == 0) { index++; continue; }
                byte[] pkh;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    pkh = sha.ComputeHash(spki);
                }
                var peerId = await _keyStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken).ConfigureAwait(false);
                if (peerId is null)
                {
                    _logger.LogWarning("[CreateGroupConversation] SPKI[{Index}] did not resolve to a known PeerId (pkh={Pkh})", index, Convert.ToBase64String(pkh));
                }
                else
                {
                    participants.Add(new Percolator.Chat.ValueObjects.ParticipantId(peerId.Value));
                }
                index++;
            }

            if (participants.Count < 2)
                throw new InvalidOperationException("A group conversation must have at least two resolved participants.");

            // Create group
            await _conversations.CreateGroupAsync(request.GroupConversationGuid, request.SelfIdentityId, participants, request.Name).ConfigureAwait(false);

            // Read back conversation id
            var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId).ConfigureAwait(false);
            if (convo is null)
                throw new InvalidOperationException("Conversation not found immediately after creation.");

            // Seed creator admin key
            var now = DateTimeOffset.UtcNow;
            await _adminKeys.AddKeyAsync(convo.Id.Value, new Percolator.Chat.ValueObjects.AdminPublicKey(request.CreatorIdentityKeySpki), now, cancellationToken).ConfigureAwait(false);
        }
    }
}
