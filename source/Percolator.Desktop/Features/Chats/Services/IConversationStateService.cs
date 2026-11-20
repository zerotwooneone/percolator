// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using ObservableCollections;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Free-threaded presentation state service managing the active persona's conversation directory,
/// direct peer-to-peer chats, relayed conversations, and staged outbound pending connections ([UISS-001], [UISS-005]).
/// </summary>
public interface IConversationStateService : IDisposable
{
    /// <summary>
    /// Thread-safe canonical synchronized projection of conversations and pending connections.
    /// Bound to the Right Sidebar view via .ToNotifyCollectionChanged() ([UISS-005], [VM-019]).
    /// </summary>
    ISynchronizedView<ConversationSummaryModel, ConversationSummaryModel> Conversations { get; }

    /// <summary>
    /// Emits when an outbound pending connection successfully completes its handshake.
    /// </summary>
    Observable<OutboundPendingConnectionModel> ConnectionSucceeded { get; }

    /// <summary>
    /// Emits when an outbound connection request is cancelled by the user.
    /// </summary>
    Observable<PublicIdentityId> ConnectionCancelled { get; }

    /// <summary>
    /// Loads conversation snapshots from SQLite CQRS query services for the specified persona.
    /// </summary>
    Task LoadConversationsForPersonaAsync(PublicIdentityId personaId, CancellationToken ct = default);

    /// <summary>
    /// Stages an offline peer into the conversation directory with a Pending status and enqueues background retry.
    /// </summary>
    void StagePendingOutbound(OutboundPendingConnectionModel model);

    /// <summary>
    /// Cancels an active outbound connection request, removing the pending item and aborting background retries.
    /// </summary>
    void CancelOutboundRequest(PublicIdentityId peerId);

    /// <summary>
    /// Checks whether the specified peer has a staged pending outbound connection.
    /// </summary>
    bool IsPending(PublicIdentityId peerId);

    /// <summary>
    /// Registers or updates an established direct conversation with a peer.
    /// </summary>
    void RegisterDirectConversation(PublicIdentityId peerId, string displayName, ChannelId? channelId = null);
}
