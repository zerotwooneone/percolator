using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;
using R3;
using Desktop.Wpf.Features.Chat.State;
using Percolator.Application.Identity;
using Percolator.Application.Chat;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Desktop.Wpf.Features.Chat;

public interface IChatReloadCoordinator : IDisposable
{
    void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId);
    void TriggerReloadForSession(DirectSessionId sessionId);
}

public sealed class ChatReloadCoordinator : IChatReloadCoordinator
{
    private sealed record ReloadTrigger(ConversationId? ConversationId, int? SelfIdentityId, DirectSessionId SessionId);

    private readonly Subject<ReloadTrigger> _reloadTrigger = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatStateService _state;
    private readonly Percolator.Application.Chat.ISelfIdentityQueries _selfIdentityQueries;
    private readonly DisposableBag _bag;

    public ChatReloadCoordinator(
        IServiceScopeFactory scopeFactory,
        ChatStateService state,
        ActiveIdentityContext activeIdentity,
        Percolator.Application.Chat.ISelfIdentityQueries selfIdentityQueries,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _selfIdentityQueries = selfIdentityQueries;

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(50), timeProvider)
            .SelectAwait(async (trigger, ct) =>
            {
                if (trigger.ConversationId.HasValue && trigger.SelfIdentityId.HasValue)
                {
                    await ReloadCoreAsync(trigger.ConversationId.Value, trigger.SelfIdentityId.Value, trigger.SessionId, ct).ConfigureAwait(false);
                }
                else
                {
                    await ReloadFromSessionAsync(trigger.SessionId, ct).ConfigureAwait(false);
                }
                return Unit.Default;
            }, AwaitOperation.Drop)
            .Subscribe()
            .AddTo(ref _bag);
    }

    public void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId)
        => _reloadTrigger.OnNext(new ReloadTrigger(conversationId, selfIdentityId, sessionId));

    public void TriggerReloadForSession(DirectSessionId sessionId)
        => _reloadTrigger.OnNext(new ReloadTrigger(null, null, sessionId));

    private async Task ReloadFromSessionAsync(DirectSessionId sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IDirectConversationResolver>();
        var lookupKey = ConversationLookupKey.ForDirectSession(sessionId.Value);
        var resolution = await resolver.ResolveAsync(lookupKey, ct).ConfigureAwait(false);

        await SyncConversationToStateAsync(resolution, sessionId, ct).ConfigureAwait(false);
    }

    private async Task ReloadCoreAsync(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageQueries = scope.ServiceProvider.GetRequiredService<IConversationMessageQueries>();
        var messages = await messageQueries.GetMessagesAsync(conversationId, selfIdentityId, ct).ConfigureAwait(false);

        var publicIdentityId = await _selfIdentityQueries.GetSelfIdentityPublicKeyAsync(new SelfId((uint)selfIdentityId), ct).ConfigureAwait(false);
        var publicIdentityIdValue = publicIdentityId?.Value ?? throw new InvalidOperationException("Public identity ID not found");
        
        var snapshots = messages.Select(m => new ChatMessageSnapshot(
            Id: new PublicMessageId(m.MessageId),
            Author: m.SenderId == publicIdentityIdValue ? "Me" : "Peer",
            Text: m.Content,
            Timestamp: m.Timestamp,
            IsOwn: m.SenderId == publicIdentityIdValue,
            IsDelivered: false,
            IsRead: false
        )).ToList();

        _state.SyncMessages(sessionId, snapshots);
    }

    private async Task SyncConversationToStateAsync(DirectConversationResolution resolution, DirectSessionId sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var messageQueries = scope.ServiceProvider.GetRequiredService<IConversationMessageQueries>();
        var selfIdentityIdInt = (int)resolution.SelfIdentityId.Value;
        var messages = await messageQueries.GetMessagesAsync(resolution.Conversation.Id, selfIdentityIdInt, ct).ConfigureAwait(false);

        var publicIdentityId = await _selfIdentityQueries.GetSelfIdentityPublicKeyAsync(new SelfId((uint)selfIdentityIdInt), ct).ConfigureAwait(false);
        var publicIdentityIdValue = publicIdentityId?.Value ?? throw new InvalidOperationException("Public identity ID not found");
        
        var snapshots = messages.Select(m => new ChatMessageSnapshot(
            Id: new PublicMessageId(m.MessageId),
            Author: m.SenderId == publicIdentityIdValue ? "Me" : "Peer",
            Text: m.Content,
            Timestamp: m.Timestamp,
            IsOwn: m.SenderId == publicIdentityIdValue,
            IsDelivered: false,
            IsRead: false
        )).ToList();

        _state.SyncMessages(sessionId, snapshots);
    }

    public void Dispose()
    {
        _reloadTrigger.Dispose();
        _bag.Dispose();
    }
}
