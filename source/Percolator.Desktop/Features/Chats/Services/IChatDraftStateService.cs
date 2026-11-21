// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Free-threaded UI state service managing volatile in-memory message drafts,
/// cursor positions, pending attachments, and per-conversation expiration timer settings ([UISS-001]).
/// Guarantees that draft text is never written to unencrypted disk caches or relational stores.
/// </summary>
public interface IChatDraftStateService : IDisposable
{
    /// <summary>
    /// Emits when a draft is updated or cleared for a channel.
    /// </summary>
    Observable<ChannelId> DraftChanged { get; }

    /// <summary>
    /// Retrieves the in-memory draft for the specified persona and channel.
    /// Returns empty draft if none has been saved.
    /// </summary>
    ChatDraftModel GetDraft(PublicIdentityId personaId, ChannelId channelId);

    /// <summary>
    /// Saves or updates the volatile draft in memory for the specified persona and channel.
    /// </summary>
    void SaveDraft(PublicIdentityId personaId, ChannelId channelId, ChatDraftModel draft);

    /// <summary>
    /// Clears the volatile draft for the specified persona and channel upon message dispatch.
    /// </summary>
    void ClearDraft(PublicIdentityId personaId, ChannelId channelId);

    /// <summary>
    /// Zeroizes and removes all volatile drafts associated with the specified persona ([UISS-007]).
    /// </summary>
    void ClearAllForPersona(PublicIdentityId personaId);
}
