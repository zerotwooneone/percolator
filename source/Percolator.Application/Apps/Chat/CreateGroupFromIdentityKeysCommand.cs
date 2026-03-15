using System.Security.Cryptography;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Chat.App;
using Percolator.Identity;
using Google.Protobuf;
using Percolator.Contracts;
using Percolator.Application.Network;

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
        private readonly IRemoteEnvelopeSender _sender;

        public CreateGroupFromIdentityKeysHandler(ILogger<CreateGroupFromIdentityKeysHandler> logger, IPeerPublicSigningKeyStore keyStore, IConversationRepository conversations, IGroupAdminKeyStore adminKeys, IRemoteEnvelopeSender sender)
        {
            _logger = logger;
            _keyStore = keyStore;
            _conversations = conversations;
            _adminKeys = adminKeys;
            _sender = sender;
        }


        public async Task Handle(CreateGroupFromIdentityKeysCommand request, CancellationToken cancellationToken)
        {
            if (request.CreatorIdentityKeySpki.Length == 0)
                throw new InvalidOperationException("CreateGroup.creator_identity_key is required and must be non-empty.");
            _logger.LogInformation("[CreateGroup] SelfIdentityId={SelfIdentityId} GroupGuid={GroupGuid} SPKIs={Count} Name='{Name}'", request.SelfIdentityId, request.GroupConversationGuid, request.ParticipantIdentityKeysSpki?.Count ?? 0, request.Name);
            var participants = new List<ParticipantId>();
            var recipientPeerIds = new List<Percolator.Identity.PeerId>();
            int index = 0;
            foreach (var spki in request.ParticipantIdentityKeysSpki)
            {
                if (spki == null || spki.Length == 0) continue;
                byte[] pkh;
                using (var sha = SHA256.Create())
                {
                    pkh = sha.ComputeHash(spki);
                }
                var peerId = await _keyStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken).ConfigureAwait(false);
                if (peerId is null)
                {
                    _logger.LogWarning("[CreateGroup] SPKI[{Index}] did not resolve to a known PeerId (pkh={Pkh})", index, Convert.ToBase64String(pkh));
                }
                else
                {
                    participants.Add(new ParticipantId(peerId.Value));
                    // Exclude creator/self from recipients by SPKI match
                    if (!spki.SequenceEqual(request.CreatorIdentityKeySpki))
                    {
                        recipientPeerIds.Add(new Percolator.Identity.PeerId(peerId.Value));
                    }
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
            await _conversations.CreateGroupAsync(request.GroupConversationGuid, request.SelfIdentityId, participants, request.Name).ConfigureAwait(false);

            // Seed initial admin key so that subsequent admin ops can be validated.
            // Only the creator should be admin initially.
            var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId).ConfigureAwait(false);
            if (convo is null)
            {
                _logger.LogWarning("[CreateGroup] Conversation not found immediately after creation for group {GroupGuid}", request.GroupConversationGuid);
                return;
            }
            try
            {
                await _adminKeys.AddKeyAsync(convo.Id.Value, new AdminPublicKey(request.CreatorIdentityKeySpki), DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[CreateGroup] Seeded initial admin key (creator) for group {GroupGuid}.", request.GroupConversationGuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CreateGroup] Failed seeding creator admin key for group {GroupGuid}", request.GroupConversationGuid);
            }

            // Build CreateGroup internal envelope to notify initial members via direct sessions
            var createGroup = new CreateGroup
            {
                GroupConversationGuid = ByteString.CopyFrom(request.GroupConversationGuid.ToByteArray()),
                Name = request.Name ?? string.Empty,
                CreatorIdentityKey = ByteString.CopyFrom(request.CreatorIdentityKeySpki)
            };
            foreach (var spki in request.ParticipantIdentityKeysSpki)
            {
                if (spki is null || spki.Length == 0) continue;
                createGroup.InitialParticipantIdentityKeys.Add(ByteString.CopyFrom(spki));
            }

            var env = new InternalEnvelope
            {
                ChatEnvelope = new ChatEnvelope
                {
                    CreateGroup = createGroup
                }
            };
            // Exclude creator/self from recipients by SPKI; include recipient PKH for host-enqueue fallback
            foreach (var spki in request.ParticipantIdentityKeysSpki)
            {
                if (spki is null || spki.Length == 0) continue;
                if (spki.SequenceEqual(request.CreatorIdentityKeySpki)) continue;
                byte[] pkh;
                using (var sha = SHA256.Create())
                {
                    pkh = sha.ComputeHash(spki);
                }
                var peerId = await _keyStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken).ConfigureAwait(false);
                if (peerId is null) continue;
                var pid = new Percolator.Identity.PeerId(peerId.Value);
                var route = new RecipientRoute(pid, pkh);
                await _sender.SendChatEnvelopeToPeerAsync(env.ChatEnvelope, route, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class NullRemoteEnvelopeSender : IRemoteEnvelopeSender
    {
        public Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
