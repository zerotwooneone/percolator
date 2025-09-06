using MediatR;
using Percolator.Chat.App.Commands;

namespace Percolator.Chat.App.Handlers;

public sealed class UpdateGroupMembershipHandler : IRequestHandler<UpdateGroupMembershipCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IConversationRepository _repository;
    private readonly ISelfParticipantIdProvider _selfProvider;

    public UpdateGroupMembershipHandler(
        IConversationResolver resolver,
        IConversationRepository repository,
        ISelfParticipantIdProvider selfProvider)
    {
        _resolver = resolver;
        _repository = repository;
        _selfProvider = selfProvider;
    }

    public async Task Handle(UpdateGroupMembershipCommand request, CancellationToken cancellationToken)
    {
        // Enforce exactly-one-key rule
        request.LookupKey.EnsureExactlyOne();

        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        var conversation = resolution.Conversation;

        // Apply removals first
        foreach (var p in request.Remove)
        {
            // If participant not present, domain will throw; this is intended to surface invalid ops
            conversation.RemoveParticipant(p);
        }

        // Apply additions
        foreach (var p in request.Add)
        {
            conversation.AddParticipant(p);
        }

        // Apply leave (remove self)
        if (request.Leave)
        {
            var self = _selfProvider.Get();
            conversation.RemoveParticipant(self);
        }

        await _repository.UpdateAsync(conversation, resolution.SelfIdentityId);
    }
}
