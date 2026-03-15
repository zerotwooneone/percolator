using MediatR;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Services
{
    public sealed class AdminOperations : IAdminOperations
    {
        private readonly IConversationResolver _resolver;
        private readonly IConversationRepository _repository;
        private readonly ISelfParticipantIdProvider _selfProvider;
        private readonly IGroupAdminKeyStore _adminKeyStore;
        private readonly IGroupAdminOpStore _adminOpStore;
        private readonly IAdminSignatureVerifier _signatureVerifier;
        private readonly IMediator _mediator;

        public AdminOperations(
            IConversationResolver resolver,
            IConversationRepository repository,
            ISelfParticipantIdProvider selfProvider,
            IGroupAdminKeyStore adminKeyStore,
            IGroupAdminOpStore adminOpStore,
            IAdminSignatureVerifier signatureVerifier,
            IMediator mediator)
        {
            _resolver = resolver;
            _repository = repository;
            _selfProvider = selfProvider;
            _adminKeyStore = adminKeyStore;
            _adminOpStore = adminOpStore;
            _signatureVerifier = signatureVerifier;
            _mediator = mediator;
        }

        public async Task GrantAdminAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            AdminPublicKey grantee,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default)
        {
            await ApplyCommonAsync(lookup, opId, sentUtc, payloadBytes, signature, async (conversationId, selfIdentityId) =>
            {
                await _adminKeyStore.AddKeyAsync(conversationId, grantee, sentUtc, ct);
            }, ct);
        }

        public async Task RevokeAdminAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            AdminPublicKey grantee,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default)
        {
            await ApplyCommonAsync(lookup, opId, sentUtc, payloadBytes, signature, async (conversationId, selfIdentityId) =>
            {
                await _adminKeyStore.RevokeKeyAsync(conversationId, grantee, sentUtc, ct);
            }, ct);
        }

        public async Task UpdateGroupMembershipAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            IReadOnlyList<ParticipantId>? membersToAdd,
            IReadOnlyList<ParticipantId>? membersToRemove,
            bool? leaveGroup,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default)
        {
            await ApplyCommonAsync(lookup, opId, sentUtc, payloadBytes, signature, async (conversationId, selfIdentityId, conversation) =>
            {
                if (membersToAdd is not null)
                {
                    foreach (var p in membersToAdd)
                        conversation.AddParticipant(p);
                }
                if (membersToRemove is not null)
                {
                    foreach (var p in membersToRemove)
                    {
                        if (conversation.Participants.Contains(p))
                            conversation.RemoveParticipant(p);
                    }
                }
                if (leaveGroup == true)
                {
                    var self = _selfProvider.Get();
                    if (conversation.Participants.Contains(self))
                        conversation.RemoveParticipant(self);
                }

                await _repository.UpdateAsync(conversation, selfIdentityId);

                // Record acting admin for this membership-changing op
                var acting = _selfProvider.Get();
                await _adminOpStore.SetActingAdminAsync(conversationId, opId, acting.Value, ct);

                // Notify application to consider group key updates
                await _mediator.Publish(new GroupMembershipChangedNotification(conversation.Id.Value), ct);
            }, ct);
        }

        public async Task UpdateGroupInfoAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            string? newGroupName,
            ByteArrayRecord? newGroupAvatar,
            byte[] signature,
            byte[] payloadBytes,
            CancellationToken ct = default)
        {
            await ApplyCommonAsync(lookup, opId, sentUtc, payloadBytes, signature, async (conversationId, selfIdentityId, conversation) =>
            {
                if (newGroupName is not null)
                {
                    conversation.ChangeName(newGroupName);
                }
                // Avatar storage omitted in domain
                await _repository.UpdateAsync(conversation, selfIdentityId);
            }, ct);
        }

        private async Task ApplyCommonAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            byte[] payloadBytes,
            byte[] signature,
            Func<Guid, int, Task> applyAsync,
            CancellationToken ct)
        {
            await ApplyCommonAsync(lookup, opId, sentUtc, payloadBytes, signature,
                async (conversationId, selfIdentityId, _) => await applyAsync(conversationId, selfIdentityId), ct);
        }

        private async Task ApplyCommonAsync(
            ConversationLookupKey lookup,
            Guid opId,
            DateTimeOffset sentUtc,
            byte[] payloadBytes,
            byte[] signature,
            Func<Guid, int, Conversation, Task> applyAsync,
            CancellationToken ct)
        {
            if (opId == Guid.Empty)
                throw new InvalidOperationException("Admin operation OpId is required");

            lookup.EnsureExactlyOne();
            var resolution = await _resolver.ResolveAsync(lookup, ct);
            var conversation = resolution.Conversation;
            var conversationId = conversation.Id.Value;

            // Idempotency
            var inserted = await _adminOpStore.TryAddAsync(conversationId, opId, sentUtc, ct);
            if (!inserted)
                return;

            // Historical signature validation
            var keys = await _adminKeyStore.GetKeysAsync(conversationId, ct);
            var validKeys = keys.Where(k => k.AddedAtUtc <= sentUtc && (k.RevokedAtUtc is null || sentUtc < k.RevokedAtUtc.Value)).ToList();
            if (validKeys.Count == 0)
                throw new InvalidOperationException("No admin keys were valid at the operation timestamp.");

            var verified = false;
            foreach (var k in validKeys)
            {
                if (await _signatureVerifier.VerifyAsync(k.PublicKey, payloadBytes, signature, ct))
                {
                    verified = true;
                    break;
                }
            }
            if (!verified)
                throw new InvalidOperationException("Admin signature verification failed against historical key set.");

            // Apply specific mutation
            await applyAsync(conversationId, resolution.SelfIdentityId, conversation);
        }
    }
}
