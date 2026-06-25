using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

public sealed class AcceptGroupInviteCommandHandler : IRequestHandler<AcceptGroupInviteCommand>
{
    private readonly IPendingGroupInvitationRepository _pendingGroupInvitationRepository;
    private readonly IGroupConversationRepository _groupConversationRepository;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ILogger<AcceptGroupInviteCommandHandler> _logger;
    private readonly ISelfIdentityQueries _selfIdentityQueries;

    public AcceptGroupInviteCommandHandler(
        IPendingGroupInvitationRepository pendingGroupInvitationRepository,
        IGroupConversationRepository groupConversationRepository,
        ISelfIdentityRepository selfIdentityRepository,
        ILogger<AcceptGroupInviteCommandHandler> logger,
        ISelfIdentityQueries selfIdentityQueries)
    {
        _pendingGroupInvitationRepository = pendingGroupInvitationRepository;
        _groupConversationRepository = groupConversationRepository;
        _selfIdentityRepository = selfIdentityRepository;
        _logger = logger;
        _selfIdentityQueries = selfIdentityQueries;
    }

    public async Task Handle(AcceptGroupInviteCommand request, CancellationToken cancellationToken)
    {
        // Load self identity to get peer ID
        // var selfIdentity = await _selfIdentityRepository.GetByIdAsync(new SelfId(request.SelfIdentityId), cancellationToken).ConfigureAwait(false)
        //     ?? throw new InvalidOperationException($"SelfIdentity not found for id {request.SelfIdentityId}.");

        var selfIdentityInfo = await _selfIdentityQueries.GetIdentityParticipantInfoAsync(request.SelfIdentityId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {request.SelfIdentityId}.");
        
        // Load pending invitation
        var pendingInvitation = await _pendingGroupInvitationRepository.GetByConversationIdAsync(request.ConversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No pending invitation found for conversation {request.ConversationId.Value}.");

        // Create GroupConversation with GroupState and GroupMembers
        var now = DateTimeOffset.UtcNow;
        var groupState = new GroupState(
            request.ConversationId,
            0,
            pendingInvitation.GroupName,
            now,
            now
        );

        var groupMember = new GroupMember(
            request.ConversationId,
            new GroupParticipantId(selfIdentityInfo.Pkh, null),
            GroupMemberRole.Member,
            now
        );

        var groupConversation = new GroupConversation(
            request.ConversationId,
            groupState,
            ,
            new[] { groupMember },
            pendingInvitation.GroupName
        );
        await _groupConversationRepository.AddAsync(groupConversation, request.SelfIdentityId, cancellationToken).ConfigureAwait(false);

        // Update PendingGroupInvitation status to Accepted
        pendingInvitation.Accept();
        await _pendingGroupInvitationRepository.UpdateAsync(pendingInvitation, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Accepted group invitation for conversation {ConversationId}", request.ConversationId.Value);
    }
}
