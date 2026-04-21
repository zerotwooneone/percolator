using Google.Protobuf;
using MediatR;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IRemoteEnvelopeSender _sender;
    private readonly IPublisher _publisher;
    private readonly ActiveIdentityContext _active;

    public PostTextMessageHandler(
        IConversationResolver resolver,
        IChatMessageWriter writer,
        IRemoteEnvelopeSender sender,
        IPublisher publisher,
        ActiveIdentityContext active)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _active = active ?? throw new ArgumentNullException(nameof(active));
    }

    public async Task Handle(PostTextMessageCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();

        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);
        var selfParticipantId = ((ISelfParticipantIdProvider) _active).Get();

        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            selfParticipantId,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        var chatEnvelope = new ChatEnvelope
        {
            TextMessage = new TextMessage
            {
                MessageId = ByteString.CopyFrom(request.MessageId.Value.ToByteArray()),
                Content = request.Content,
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(request.SentTimestampUtc),
            }
        };

        if (request.LookupKey.GroupConversationGuid.HasValue)
        {
            chatEnvelope.TextMessage.GroupConversationGuid = ByteString.CopyFrom(request.LookupKey.GroupConversationGuid.Value.ToByteArray());
            // For group messages, include the author's identity key (SPKI) when available
            var spki = _active.Keys?.IdentitySigningKey?.ExportSubjectPublicKeyInfo();
            if (spki is not null)
            {
                chatEnvelope.TextMessage.AuthorIdentityKey = ByteString.CopyFrom(spki);
            }
        }

        var tasks = resolution.Conversation.Participants
            .Select(participant =>
            {
                var route = new RecipientRoute(new Percolator.Identity.PeerId(participant.Value), null); // PKH is optional for direct send
                return Result.FromTask(_sender.SendChatEnvelopeToPeerAsync(chatEnvelope, route, cancellationToken));
            });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var errors = results.Where(r => !r.IsSuccess).ToList();
        if (errors.Any())
        {
            throw new AggregateException(errors.Select(r => r.Exception)!);
        }

        await _publisher.Publish(new TextMessagePostedEvent(
                resolution.Conversation.Id.Value,
                request.MessageId.Value,
                resolution.SelfIdentityId,
                resolution.Conversation.Participants
                    .Select(p => p.Value)
                    .ToList(),
                request.Content,
                request.SentTimestampUtc,
                Percolator.Chat.ValueObjects.DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)),
            cancellationToken).ConfigureAwait(false);
    }

    private record Result
    {
        public Exception? Exception { get; } = null;
        public bool IsSuccess => Exception is null;
        public static readonly Result Success = new Result();
        public static Result FromException(Exception exception) => new Result(exception);

        public static async Task<Result> FromTask(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
                return Success;
            }
            catch (Exception ex)
            {
                return FromException(ex);
            }
        }

        public Result(Exception exception)
        {
            Exception = exception;
        }

        private Result()
        {
            
        }
    }
}
