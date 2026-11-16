// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using ObservableCollections;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Features.Transports.Services;

/// <summary>
/// Stateful presentation service managing active and pending peer connection state.
/// Tracks pending outbound connections staged while remote peers are offline,
/// handles user cancellation, and surfaces reactive observables for shell and sidebar views.
/// </summary>
public interface IPeerConnectionStateService : IDisposable
{
    /// <summary>
    /// Thread-safe canonical observable collection of pending outbound direct connections.
    /// Bound to the Right Sidebar conversation list via .CreateView().
    /// </summary>
    ObservableList<OutboundPendingConnectionModel> PendingOutboundConnections { get; }

    /// <summary>
    /// Emits when an outbound pending connection successfully completes its handshake.
    /// </summary>
    Observable<OutboundPendingConnectionModel> ConnectionSucceeded { get; }

    /// <summary>
    /// Emits when an outbound connection request is cancelled by the user.
    /// </summary>
    Observable<PublicIdentityId> ConnectionCancelled { get; }

    /// <summary>
    /// Stages an offline peer into the pending outbound connections list and enqueues background retry.
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
}
