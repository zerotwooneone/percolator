using Microsoft.Extensions.DependencyInjection;
using ObservableCollections;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using R3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Chat.State;
using Percolator.Chat;

namespace Desktop.Wpf.Features.Chat;

public interface IChatReloadCoordinator : IDisposable
{
    void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId);
}

public sealed class ChatReloadCoordinator : IChatReloadCoordinator
{
    private readonly Subject<(ConversationId conversationId, int selfIdentityId)> _reloadTrigger = new();
    private readonly DisposableBag _bag = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatStateService _state;
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;

    public ChatReloadCoordinator(
        IServiceScopeFactory scopeFactory,
        ChatStateService state,
        ISelfParticipantIdProvider selfParticipantIdProvider)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _selfParticipantIdProvider = selfParticipantIdProvider;

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(50))
            .SelectAwait(async (evt, ct) =>
            {
                await ReloadCoreAsync(evt.conversationId, evt.selfIdentityId, ct).ConfigureAwait(false);
                return Unit.Default;
            }, AwaitOperation.Drop)
            .Subscribe()
            .AddTo(ref _bag);
    }

    public void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId)
    {
        _reloadTrigger.OnNext((conversationId, selfIdentityId));
    }

    private async Task ReloadCoreAsync(ConversationId conversationId, int selfIdentityId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var conversationRepository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var selfParticipantId = _selfParticipantIdProvider.Get();

        var conversation = await conversationRepository.GetByIdAsync(conversationId, selfIdentityId).ConfigureAwait(false);

        if (conversation is null)
            return;

        var snapshots = conversation.Messages.Select(m => new ChatMessageSnapshot(
            Id: m.Id.Value.ToString("N"),
            Author: m.SenderId.Value == selfParticipantId.Value ? "Me" : "Peer",
            Text: m.Content,
            Timestamp: m.Timestamp,
            IsOwn: m.SenderId.Value == selfParticipantId.Value,
            IsDelivered: false,
            IsRead: m.ReadReceipts.Any(r => r.ReaderId.Value == selfParticipantId.Value)
        )).ToList();

        var conversationKey = conversationId.Value.ToString("N");
        _state.SyncMessages(conversationKey, snapshots);
    }

    public void Dispose()
    {
        _reloadTrigger.Dispose();
        _bag.Dispose();
    }
}
