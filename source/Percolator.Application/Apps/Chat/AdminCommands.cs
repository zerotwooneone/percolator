using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using System.Linq;

namespace Percolator.Application.Apps.Chat
{
    // Red phase stubs: commands + handler signatures only
    public sealed record GrantGroupAdminAppCommand(
        int SelfIdentityId,
        Guid GroupConversationGuid,
        byte[] GranteeSpki
    ) : IRequest;

    public sealed record RevokeGroupAdminAppCommand(
        int SelfIdentityId,
        Guid GroupConversationGuid,
        byte[] GranteeSpki
    ) : IRequest;

    public sealed record UpdateGroupMembershipAppCommand(
        int SelfIdentityId,
        Guid GroupConversationGuid,
        IReadOnlyList<Guid>? MembersToAdd,
        IReadOnlyList<Guid>? MembersToRemove,
        bool? LeaveGroup
    ) : IRequest;

    public sealed record UpdateGroupInfoAppCommand(
        int SelfIdentityId,
        Guid GroupConversationGuid,
        string? NewGroupName
    ) : IRequest;

    internal sealed class GrantGroupAdminHandler : IRequestHandler<GrantGroupAdminAppCommand>
    {
        private readonly Percolator.Chat.IConversationRepository _conversations;
        private readonly Percolator.Chat.ISelfParticipantIdProvider _selfProvider;
        private readonly Percolator.Chat.App.IAdminOperations _adminOps;
        private readonly IAdminOperationDispatcher _dispatcher;
        private readonly IAdminOperationSigner _signer;
        private readonly Percolator.Chat.App.IAdminSequenceProvider _sequenceProvider;

        public GrantGroupAdminHandler(
            Percolator.Chat.IConversationRepository conversations,
            Percolator.Chat.ISelfParticipantIdProvider selfProvider,
            Percolator.Chat.App.IAdminOperations adminOps,
            IAdminOperationDispatcher dispatcher,
            IAdminOperationSigner signer,
            Percolator.Chat.App.IAdminSequenceProvider sequenceProvider)
        {
            _conversations = conversations;
            _selfProvider = selfProvider;
            _adminOps = adminOps;
            _dispatcher = dispatcher;
            _signer = signer;
            _sequenceProvider = sequenceProvider;
        }
        public Task Handle(GrantGroupAdminAppCommand request, CancellationToken cancellationToken)
        {
            if (request.GranteeSpki is null || request.GranteeSpki.Length == 0)
                throw new InvalidOperationException("Grantee public key (SPKI) is required.");
            var lookup = Percolator.Chat.App.ConversationLookupKey.ForGroup(request.GroupConversationGuid);
            var now = DateTimeOffset.UtcNow;
            var opId = Guid.NewGuid();

            var seqTask = _sequenceProvider.NextAsync(request.GroupConversationGuid, cancellationToken);
            var signTask = seqTask.ContinueWith(t => _signer.SignGrantAsync(request.GroupConversationGuid, opId, now, request.GranteeSpki, t.Result, cancellationToken), cancellationToken).Unwrap();
            var grantee = new Percolator.Chat.ValueObjects.AdminPublicKey(request.GranteeSpki);

            return signTask.ContinueWith(t =>
            {
                var (payloadBytes, signature) = t.Result;
                return _adminOps.GrantAdminAsync(lookup, opId, now, grantee, signature, payloadBytes, cancellationToken);
            }, cancellationToken).Unwrap()
            .ContinueWith(async _ =>
            {
                var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId);
                if (convo is null) return;

                var senderParticipant = _selfProvider.Get();
                var senderPeerId = new Percolator.Identity.PeerId(senderParticipant.Value);
                var recipients = convo.Participants
                    .Select(p => new Percolator.Identity.PeerId(p.Value))
                    .ToList();

                var (_, signature2) = await signTask;
                var adminSeq = await seqTask;
                await _dispatcher.DispatchGrantAdminAsync(
                    senderPeerId,
                    request.GroupConversationGuid,
                    opId,
                    now,
                    adminSequenceNumber: adminSeq,
                    granteePublicKeySpki: request.GranteeSpki,
                    signature: signature2,
                    recipientPeerIds: recipients,
                    ct: cancellationToken);
            }, cancellationToken).Unwrap();
        }
    }

    internal sealed class RevokeGroupAdminHandler : IRequestHandler<RevokeGroupAdminAppCommand>
    {
        private readonly Percolator.Chat.IConversationRepository _conversations;
        private readonly Percolator.Chat.ISelfParticipantIdProvider _selfProvider;
        private readonly Percolator.Chat.App.IAdminOperations _adminOps;
        private readonly IAdminOperationDispatcher _dispatcher;
        private readonly IAdminOperationSigner _signer;
        private readonly Percolator.Chat.App.IAdminSequenceProvider _sequenceProvider;

        public RevokeGroupAdminHandler(
            Percolator.Chat.IConversationRepository conversations,
            Percolator.Chat.ISelfParticipantIdProvider selfProvider,
            Percolator.Chat.App.IAdminOperations adminOps,
            IAdminOperationDispatcher dispatcher,
            IAdminOperationSigner signer,
            Percolator.Chat.App.IAdminSequenceProvider sequenceProvider)
        {
            _conversations = conversations;
            _selfProvider = selfProvider;
            _adminOps = adminOps;
            _dispatcher = dispatcher;
            _signer = signer;
            _sequenceProvider = sequenceProvider;
        }
        public Task Handle(RevokeGroupAdminAppCommand request, CancellationToken cancellationToken)
        {
            if (request.GranteeSpki is null || request.GranteeSpki.Length == 0)
                throw new InvalidOperationException("Grantee public key (SPKI) is required.");

            var lookup = Percolator.Chat.App.ConversationLookupKey.ForGroup(request.GroupConversationGuid);
            var now = DateTimeOffset.UtcNow;
            var opId = Guid.NewGuid();

            var seqTask = _sequenceProvider.NextAsync(request.GroupConversationGuid, cancellationToken);
            var signTask = seqTask.ContinueWith(t => _signer.SignRevokeAsync(request.GroupConversationGuid, opId, now, request.GranteeSpki, t.Result, cancellationToken), cancellationToken).Unwrap();
            var grantee = new Percolator.Chat.ValueObjects.AdminPublicKey(request.GranteeSpki);

            return signTask.ContinueWith(t =>
            {
                var (payloadBytes, signature) = t.Result;
                return _adminOps.RevokeAdminAsync(lookup, opId, now, grantee, signature, payloadBytes, cancellationToken);
            }, cancellationToken).Unwrap()
                .ContinueWith(async _ =>
                {
                    var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId);
                    if (convo is null) return;

                    var senderParticipant = _selfProvider.Get();
                    var senderPeerId = new Percolator.Identity.PeerId(senderParticipant.Value);
                    var recipients = convo.Participants
                        .Select(p => new Percolator.Identity.PeerId(p.Value))
                        .ToList();

                    var (_, signature2) = await signTask;
                    var adminSeq = await seqTask;
                    await _dispatcher.DispatchRevokeAdminAsync(
                        senderPeerId,
                        request.GroupConversationGuid,
                        opId,
                        now,
                        adminSequenceNumber: adminSeq,
                        granteePublicKeySpki: request.GranteeSpki,
                        signature: signature2,
                        recipientPeerIds: recipients,
                        ct: cancellationToken);
                }, cancellationToken).Unwrap();
        }
    }

    internal sealed class UpdateGroupMembershipHandler : IRequestHandler<UpdateGroupMembershipAppCommand>
    {
        private readonly Percolator.Chat.IConversationRepository _conversations;
        private readonly Percolator.Chat.ISelfParticipantIdProvider _selfProvider;
        private readonly Percolator.Chat.App.IAdminOperations _adminOps;
        private readonly IAdminOperationDispatcher _dispatcher;
        private readonly IAdminOperationSigner _signer;
        private readonly Percolator.Chat.App.IAdminSequenceProvider _sequenceProvider;

        public UpdateGroupMembershipHandler(
            MediatR.IMediator mediator,
            Percolator.Chat.IConversationRepository conversations,
            Percolator.Chat.ISelfParticipantIdProvider selfProvider,
            Percolator.Chat.App.IAdminOperations adminOps,
            IAdminOperationDispatcher dispatcher,
            IAdminOperationSigner signer,
            Percolator.Chat.App.IAdminSequenceProvider sequenceProvider)
        {
            _conversations = conversations;
            _selfProvider = selfProvider;
            _adminOps = adminOps;
            _dispatcher = dispatcher;
            _signer = signer;
            _sequenceProvider = sequenceProvider;
        }
        public Task Handle(UpdateGroupMembershipAppCommand request, CancellationToken cancellationToken)
        {
            var lookup = Percolator.Chat.App.ConversationLookupKey.ForGroup(request.GroupConversationGuid);
            var now = DateTimeOffset.UtcNow;
            var opId = Guid.NewGuid();

            IReadOnlyList<Percolator.Chat.ValueObjects.ParticipantId>? add = request.MembersToAdd?.Select(id => new Percolator.Chat.ValueObjects.ParticipantId(id)).ToList();
            IReadOnlyList<Percolator.Chat.ValueObjects.ParticipantId>? remove = request.MembersToRemove?.Select(id => new Percolator.Chat.ValueObjects.ParticipantId(id)).ToList();

            var seqTask = _sequenceProvider.NextAsync(request.GroupConversationGuid, cancellationToken);
            var signTask = seqTask.ContinueWith(t => _signer.SignUpdateMembershipAsync(request.GroupConversationGuid, opId, now, request.MembersToAdd, request.MembersToRemove, request.LeaveGroup, t.Result, cancellationToken), cancellationToken).Unwrap();

            return signTask.ContinueWith(t =>
            {
                var (payloadBytes, signature) = t.Result;
                return _adminOps.UpdateGroupMembershipAsync(lookup, opId, now, add, remove, request.LeaveGroup, signature, payloadBytes, cancellationToken);
            }, cancellationToken).Unwrap()
                .ContinueWith(async _ =>
                {
                    var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId);
                    if (convo is null) return;

                    var senderParticipant = _selfProvider.Get();
                    var senderPeerId = new Percolator.Identity.PeerId(senderParticipant.Value);
                    var recipients = convo.Participants
                        .Select(p => new Percolator.Identity.PeerId(p.Value))
                        .ToList();

                    var (payloadBytes2, signature2) = await signTask;
                    var adminSeq = await seqTask;
                    await _dispatcher.DispatchUpdateGroupMembershipAsync(
                        senderPeerId,
                        request.GroupConversationGuid,
                        opId,
                        now,
                        adminSequenceNumber: adminSeq,
                        membersToAdd: request.MembersToAdd,
                        membersToRemove: request.MembersToRemove,
                        leaveGroup: request.LeaveGroup,
                        signature: signature2,
                        recipientPeerIds: recipients,
                        ct: cancellationToken);
                }, cancellationToken).Unwrap();
        }
    }

    internal sealed class UpdateGroupInfoHandler : IRequestHandler<UpdateGroupInfoAppCommand>
    {
        private readonly Percolator.Chat.IConversationRepository _conversations;
        private readonly Percolator.Chat.ISelfParticipantIdProvider _selfProvider;
        private readonly Percolator.Chat.App.IAdminOperations _adminOps;
        private readonly IAdminOperationDispatcher _dispatcher;
        private readonly IAdminOperationSigner _signer;
        private readonly Percolator.Chat.App.IAdminSequenceProvider _sequenceProvider;

        public UpdateGroupInfoHandler(
            Percolator.Chat.IConversationRepository conversations,
            Percolator.Chat.ISelfParticipantIdProvider selfProvider,
            Percolator.Chat.App.IAdminOperations adminOps,
            IAdminOperationDispatcher dispatcher,
            IAdminOperationSigner signer,
            Percolator.Chat.App.IAdminSequenceProvider sequenceProvider)
        {
            _conversations = conversations;
            _selfProvider = selfProvider;
            _adminOps = adminOps;
            _dispatcher = dispatcher;
            _signer = signer;
            _sequenceProvider = sequenceProvider;
        }
        public Task Handle(UpdateGroupInfoAppCommand request, CancellationToken cancellationToken)
        {
            var lookup = Percolator.Chat.App.ConversationLookupKey.ForGroup(request.GroupConversationGuid);
            var now = DateTimeOffset.UtcNow;
            var opId = Guid.NewGuid();

            var seqTask = _sequenceProvider.NextAsync(request.GroupConversationGuid, cancellationToken);
            var signTask = seqTask.ContinueWith(t => _signer.SignUpdateInfoAsync(request.GroupConversationGuid, opId, now, request.NewGroupName, null, t.Result, cancellationToken), cancellationToken).Unwrap();

            return signTask.ContinueWith(t =>
            {
                var (payloadBytes, signature) = t.Result;
                return _adminOps.UpdateGroupInfoAsync(lookup, opId, now, request.NewGroupName, null, signature, payloadBytes, cancellationToken);
            }, cancellationToken).Unwrap()
                .ContinueWith(async _ =>
                {
                    var convo = await _conversations.GetByGroupGuidAsync(request.GroupConversationGuid, request.SelfIdentityId);
                    if (convo is null) return;

                    var senderParticipant = _selfProvider.Get();
                    var senderPeerId = new Percolator.Identity.PeerId(senderParticipant.Value);
                    var recipients = convo.Participants
                        .Select(p => new Percolator.Identity.PeerId(p.Value))
                        .ToList();

                    var (payloadBytes2, signature2) = await signTask;
                    var adminSeq = await seqTask;
                    await _dispatcher.DispatchUpdateGroupInfoAsync(
                        senderPeerId,
                        request.GroupConversationGuid,
                        opId,
                        now,
                        adminSequenceNumber: adminSeq,
                        newGroupName: request.NewGroupName,
                        newGroupAvatar: null,
                        signature: signature2,
                        recipientPeerIds: recipients,
                        ct: cancellationToken);
                }, cancellationToken).Unwrap();
        }
    }
}
