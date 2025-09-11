using MediatR;
using Percolator.Chat.App.Commands;
using Percolator.Chat.ValueObjects;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Chat.App.Handlers
{
    // Applies a SignedAdminOperation after verifying the admin signature against historical key validity.
    public class ApplySignedAdminOperationHandler : IRequestHandler<ApplySignedAdminOperationCommand>
    {
        private readonly IConversationResolver _resolver;
        private readonly IConversationRepository _repository;
        private readonly ISelfParticipantIdProvider _selfProvider;
        private readonly IGroupAdminKeyStore _adminKeyStore;
        private readonly IGroupAdminOpStore _adminOpStore;
        private readonly IAdminSignatureVerifier _signatureVerifier;
        private readonly IMediator _mediator;

        public ApplySignedAdminOperationHandler(
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

        public async Task Handle(ApplySignedAdminOperationCommand request, CancellationToken cancellationToken)
        {
            // Basic invariants
            if (request.OpId == Guid.Empty)
                throw new InvalidOperationException("ApplySignedAdminOperation.OpId is required");

            request.Lookup.EnsureExactlyOne();
            var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);
            var conversation = resolution.Conversation;
            var conversationId = conversation.Id.Value;

            // Idempotency: if op already applied, no-op
            var inserted = await _adminOpStore.TryAddAsync(conversationId, request.OpId, request.SentUtc, cancellationToken);
            if (!inserted)
            {
                return; // already applied
            }

            // Historical signature validation
            var keys = await _adminKeyStore.GetKeysAsync(conversationId, cancellationToken);
            var validKeys = keys.Where(k => k.AddedAtUtc <= request.SentUtc && (k.RevokedAtUtc is null || request.SentUtc < k.RevokedAtUtc.Value)).ToList();
            if (validKeys.Count == 0)
            {
                throw new InvalidOperationException("No admin keys were valid at the operation timestamp.");
            }

            var verified = false;
            foreach (var k in validKeys)
            {
                if (await _signatureVerifier.VerifyAsync(k.PublicKey, request.PayloadBytes, request.Signature.Bytes, cancellationToken))
                {
                    verified = true;
                    break;
                }
            }
            if (!verified)
            {
                throw new InvalidOperationException("Admin signature verification failed against historical key set.");
            }

            // Apply operation to domain + admin keys
            switch (request.Kind)
            {
                case AdminOperationKind.GrantAdmin:
                    if (request.Grantee is null)
                        throw new InvalidOperationException("GrantAdmin requires a grantee public key");
                    await _adminKeyStore.AddKeyAsync(conversationId, request.Grantee.Value, request.SentUtc, cancellationToken);
                    break;

                case AdminOperationKind.RevokeAdmin:
                    if (request.Grantee is null)
                        throw new InvalidOperationException("RevokeAdmin requires a grantee public key");
                    await _adminKeyStore.RevokeKeyAsync(conversationId, request.Grantee.Value, request.SentUtc, cancellationToken);
                    break;

                case AdminOperationKind.UpdateGroupMembership:
                    // Add → Remove → optional self-leave
                    if (request.MembersToAdd is not null)
                    {
                        foreach (var p in request.MembersToAdd)
                        {
                            conversation.AddParticipant(p);
                        }
                    }
                    if (request.MembersToRemove is not null)
                    {
                        foreach (var p in request.MembersToRemove)
                        {
                            if (conversation.Participants.Contains(p))
                            {
                                conversation.RemoveParticipant(p);
                            }
                        }
                    }
                    if (request.LeaveGroup == true)
                    {
                        var self = _selfProvider.Get();
                        if (conversation.Participants.Contains(self))
                        {
                            conversation.RemoveParticipant(self);
                        }
                    }
                    await _repository.UpdateAsync(conversation, resolution.SelfIdentityId);
                    // Notify application layer to consider distributing a new group key version
                    await _mediator.Publish(new GroupMembershipChangedNotification(conversation.Id.Value), cancellationToken);
                    break;

                case AdminOperationKind.UpdateGroupInfo:
                    if (request.NewGroupName is not null)
                    {
                        conversation.ChangeName(request.NewGroupName);
                    }
                    // Avatar would be stored externally or in infra; domain keeps minimal name for now.
                    await _repository.UpdateAsync(conversation, resolution.SelfIdentityId);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported admin operation kind: {request.Kind}");
            }
        }
    }
}
