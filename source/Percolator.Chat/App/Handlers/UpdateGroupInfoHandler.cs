using MediatR;
using Percolator.Chat.App.Commands;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Handlers;

public sealed class UpdateGroupInfoHandler : IRequestHandler<UpdateGroupInfoCommand>
{
    private readonly IGroupConversationRepository _repository;

    public UpdateGroupInfoHandler(IGroupConversationRepository repository)
    {
        _repository = repository;
    }

    public async Task Handle(UpdateGroupInfoCommand request, CancellationToken cancellationToken)
    {
        var groupConversation = await _repository.GetByIdAsync(request.ConversationId, request.SelfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"Group conversation not found for id {request.ConversationId.Value}.");

        if (request.NewName != null)
        {
            groupConversation.ChangeName(request.NewName, DateTimeOffset.UtcNow);
        }

        await _repository.UpdateAsync(groupConversation, request.SelfIdentityId, cancellationToken);
    }
}
