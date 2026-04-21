using Microsoft.Extensions.DependencyInjection;
using ObservableCollections;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Percolator.Network;
using R3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Chat.State;
using Percolator.Application.Identity;
using Percolator.Chat;

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
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;
    private readonly ActiveIdentityContext _activeIdentity;
    private readonly DisposableBag _bag;

    public ChatReloadCoordinator(
        IServiceScopeFactory scopeFactory,
        ChatStateService state,
        ISelfParticipantIdProvider selfParticipantIdProvider,
        ActiveIdentityContext activeIdentity,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _selfParticipantIdProvider = selfParticipantIdProvider;
        _activeIdentity = activeIdentity;

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
        var resolver = scope.ServiceProvider.GetRequiredService<IConversationResolver>();
        var lookupKey = ConversationLookupKey.ForDirectSession(sessionId.Value);
        var resolution = await resolver.ResolveAsync(lookupKey, ct).ConfigureAwait(false);

        SyncConversationToState(resolution, sessionId);
    }

    private async Task ReloadCoreAsync(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var conversation = await repo.GetByIdAsync(conversationId, selfIdentityId).ConfigureAwait(false);
        if (conversation is null) return;

        var selfParticipantId = _selfParticipantIdProvider.Get();
        var snapshots = conversation.Messages.Select(m => new ChatMessageSnapshot(
            Id: new MessageId(m.Id.Value),
            Author: m.SenderId == selfParticipantId ? "Me" : "Peer",
            Text: m.Content,
            Timestamp: m.Timestamp,
            IsOwn: m.SenderId == selfParticipantId,
            IsDelivered: false,
            IsRead: m.ReadReceipts.Any(r => r.ReaderId == selfParticipantId)
        )).ToList();

        _state.SyncMessages(sessionId, snapshots);
    }

    private void SyncConversationToState(ConversationResolution resolution, DirectSessionId sessionId)
    {
        var selfParticipantId = _selfParticipantIdProvider.Get();
        var snapshots = resolution.Conversation.Messages.Select(m => new ChatMessageSnapshot(
            Id: new MessageId(m.Id.Value),
            Author: m.SenderId == selfParticipantId ? "Me" : "Peer",
            Text: m.Content,
            Timestamp: m.Timestamp,
            IsOwn: m.SenderId == selfParticipantId,
            IsDelivered: false,
            IsRead: m.ReadReceipts.Any(r => r.ReaderId == selfParticipantId)
        )).ToList();

        _state.SyncMessages(sessionId, snapshots);
    }

    public void Dispose()
    {
        _reloadTrigger.Dispose();
        _bag.Dispose();
    }
}
