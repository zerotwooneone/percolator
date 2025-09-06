using MediatR;
using Percolator.Chat.App.Commands;

namespace Percolator.Chat.App.Handlers;

public sealed class UpdateGroupInfoHandler : IRequestHandler<UpdateGroupInfoCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IConversationRepository _repository;

    public UpdateGroupInfoHandler(IConversationResolver resolver, IConversationRepository repository)
    {
        _resolver = resolver;
        _repository = repository;
    }

    public async Task Handle(UpdateGroupInfoCommand request, CancellationToken cancellationToken)
    {
        // Enforce exactly-one-key rule
        request.LookupKey.EnsureExactlyOne();

        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        var conversation = resolution.Conversation;

        if (request.NewName != null)
        {
            conversation.ChangeName(request.NewName);
        }

        await _repository.UpdateAsync(conversation, resolution.SelfIdentityId);
    }
}
