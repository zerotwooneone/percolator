using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat;

public sealed class DeclineGroupInviteCommandHandler : IRequestHandler<DeclineGroupInviteCommand>
{
    private readonly IPendingGroupInvitationRepository _pendingGroupInvitationRepository;
    private readonly ILogger<DeclineGroupInviteCommandHandler> _logger;

    public DeclineGroupInviteCommandHandler(
        IPendingGroupInvitationRepository pendingGroupInvitationRepository,
        ILogger<DeclineGroupInviteCommandHandler> logger)
    {
        _pendingGroupInvitationRepository = pendingGroupInvitationRepository;
        _logger = logger;
    }

    public async Task Handle(DeclineGroupInviteCommand request, CancellationToken cancellationToken)
    {
        // Load pending invitation
        var pendingInvitation = await _pendingGroupInvitationRepository.GetByConversationIdAsync(request.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException($"No pending invitation found for conversation {request.ConversationId.Value}.");

        // Update PendingGroupInvitation status to Declined
        pendingInvitation.Decline();
        await _pendingGroupInvitationRepository.UpdateAsync(pendingInvitation, cancellationToken);

        _logger.LogInformation("Declined group invitation for conversation {ConversationId}", request.ConversationId.Value);
    }
}
