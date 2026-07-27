using MediatR;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class UpdateGroupInfoHandler : IRequestHandler<UpdateGroupInfoCommand>
{
    private readonly IGroupConversationRepository _repository;

    public UpdateGroupInfoHandler(IGroupConversationRepository repository)
    {
        _repository = repository;
    }

    public async Task Handle(UpdateGroupInfoCommand request, CancellationToken cancellationToken)
    {
        var groupConversation = await _repository.GetByIdAsync(request.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException($"Group conversation not found for id {request.ConversationId.Value}.");

        if (request.NewName != null)
        {
            var groupName = new GroupName(request.NewName);
            groupConversation.RenameGroup(request.ActorParticipantId, groupName);
        }

        await _repository.UpdateAsync(groupConversation, cancellationToken);
    }
}
