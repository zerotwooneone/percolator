using MediatR;
using Percolator.Chat.App;

namespace Percolator.Chat.App.Commands;

public sealed class PostEmojiAnnotationHandler : IRequestHandler<PostEmojiAnnotationCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;

    public PostEmojiAnnotationHandler(IConversationResolver resolver, IChatMessageWriter writer)
    {
        _resolver = resolver;
        _writer = writer;
    }

    public async Task Handle(PostEmojiAnnotationCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        await _writer.AddEmojiAnnotationAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.MessageId,
            request.Emoji,
            request.SentTimestampUtc,
            cancellationToken);
    }
}
