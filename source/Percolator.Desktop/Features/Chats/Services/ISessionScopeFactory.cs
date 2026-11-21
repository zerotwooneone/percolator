// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Domain.Channels.ValueObjects;

/// <summary>
/// Session-scoped factory managing bounded LRU-cached child DI scopes and ViewModels
/// for active conversations ([DEC-LRU-SESSION-SCOPES], [UISS-007]).
/// </summary>
public interface ISessionScopeFactory : IDisposable
{
    /// <summary>
    /// Retrieves an existing active conversation ViewModel or instantiates a new one
    /// inside an isolated child DI scope. Evicts the least recently used scope if capacity exceeds 5.
    /// </summary>
    ChatViewModel GetOrCreateChatViewModel(ChannelId channelId);

    /// <summary>
    /// Explicitly removes and disposes a specific conversation scope if cached.
    /// </summary>
    void EvictChat(ChannelId channelId);

    /// <summary>
    /// Disposes and clears all cached conversation scopes (e.g. on persona switch or session lock).
    /// </summary>
    void ClearAll();
}
