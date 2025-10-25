using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Chat.App;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat
{
    public sealed record CreateGroupFromIdentityKeysCommand(
        int SelfIdentityId,
        Guid GroupConversationGuid,
        IReadOnlyList<byte[]> ParticipantIdentityKeysSpki,
        string? Name,
        byte[] CreatorIdentityKeySpki
    ) : IRequest;

    internal sealed class CreateGroupFromIdentityKeysHandler : IRequestHandler<CreateGroupFromIdentityKeysCommand>
    {
        private readonly ILogger<CreateGroupFromIdentityKeysHandler> _logger;
        private readonly IPeerPublicSigningKeyStore _keyStore;
        private readonly IConversationRepository _conversations;
        private readonly IGroupAdminKeyStore _adminKeys;

        public CreateGroupFromIdentityKeysHandler(ILogger<CreateGroupFromIdentityKeysHandler> logger, IPeerPublicSigningKeyStore keyStore, IConversationRepository conversations, IGroupAdminKeyStore adminKeys)
        {
            _logger = logger;
            _keyStore = keyStore;
            _conversations = conversations;
            _adminKeys = adminKeys;
        }

        public async Task Handle(CreateGroupFromIdentityKeysCommand request, CancellationToken cancellationToken)
        {
            if (request.CreatorIdentityKeySpki.Length == 0)
                throw new InvalidOperationException("CreateGroup.creator_identity_key is required and must be non-empty.");
            _logger.LogInformation("[CreateGroup] SelfIdentityId={SelfIdentityId} GroupGuid={GroupGuid} SPKIs={Count} Name='{Name}'", request.SelfIdentityId, request.GroupConversationGuid, request.ParticipantIdentityKeysSpki?.Count ?? 0, request.Name);
            var participants = new List<ParticipantId>();
            int index = 0;
            foreach (var spki in request.ParticipantIdentityKeysSpki)
            {
                if (spki == null || spki.Length == 0) continue;
                byte[] pkh;
                using (var sha = SHA256.Create())
                {
                    pkh = sha.ComputeHash(spki);
                }
                var peerId = await _keyStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken);
                if (peerId is null)
                {
                    _logger.LogWarning("[CreateGroup] SPKI[{Index}] did not resolve to a known PeerId (pkh={Pkh})", index, Convert.ToBase64String(pkh));
                }
                else
                {
                    participants.Add(new ParticipantId(peerId.Value));
                    _logger.LogDebug("[CreateGroup] SPKI[{Index}] -> PeerId={PeerId}", index, peerId.Value);
                }
                index++;
            }
            // Deduplicate participants by PeerId
            participants = participants.Distinct().ToList();

            if (participants.Count < 2)
            {
                _logger.LogWarning("[CreateGroup] Aborting group creation. Resolved participants = {Count} (< 2).", participants.Count);
                return;
            }
            _logger.LogInformation("[CreateGroup] Creating group with {Count} participants.", participants.Count);
            await _conversations.CreateGroupAsync(request.GroupConversationGuid, request.SelfIdentityId, participants, request.Name);

            // Seed initial admin key so that subsequent admin ops can be validated.
            // Only the creator should be admin initially.
            var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId);
            if (convo is null)
            {
                _logger.LogWarning("[CreateGroup] Conversation not found immediately after creation for group {GroupGuid}", request.GroupConversationGuid);
                return;
            }
            try
            {
                await _adminKeys.AddKeyAsync(convo.Id.Value, new AdminPublicKey(request.CreatorIdentityKeySpki), DateTimeOffset.UtcNow, cancellationToken);
                _logger.LogInformation("[CreateGroup] Seeded initial admin key (creator) for group {GroupGuid}.", request.GroupConversationGuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CreateGroup] Failed seeding creator admin key for group {GroupGuid}", request.GroupConversationGuid);
            }
        }
    }
}
