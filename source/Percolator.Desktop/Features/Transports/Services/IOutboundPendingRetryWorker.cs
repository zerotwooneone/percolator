// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Features.Transports.Services;

/// <summary>
/// Background hosted service managing asynchronous retry loops with exponential backoff
/// for outbound direct peer connections staged while the remote peer is offline.
/// </summary>
public interface IOutboundPendingRetryWorker : IHostedService, IDisposable
{
    /// <summary>
    /// Emits when an outbound connection attempt successfully handshakes with a direct peer.
    /// </summary>
    Observable<OutboundPendingConnectionModel> ConnectionSucceeded { get; }

    /// <summary>
    /// Set of peer IDs currently active in retry loops.
    /// </summary>
    IReadOnlyCollection<PublicIdentityId> ActiveRetryPeerIds { get; }

    /// <summary>
    /// Enqueues an offline direct peer for background retry polling.
    /// </summary>
    void EnqueueRetry(OutboundPendingConnectionModel model);

    /// <summary>
    /// Cancels background retry polling for the specified peer ID.
    /// </summary>
    void CancelRetry(PublicIdentityId peerId);

    /// <summary>
    /// Checks whether an active retry loop exists for the specified peer ID.
    /// </summary>
    bool IsRetrying(PublicIdentityId peerId);
}
