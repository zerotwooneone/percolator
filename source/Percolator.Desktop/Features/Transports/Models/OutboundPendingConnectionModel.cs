// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using Percolator.Application.Invitations;
using Percolator.Domain.Identities;

namespace Percolator.Desktop.Features.Transports.Models;

/// <summary>
/// Model representing a staged outbound connection to an offline peer.
/// Tracks peer metadata, invitation tokens, and the initial attempt timestamp for background retries.
/// </summary>
public sealed record OutboundPendingConnectionModel(
    PublicIdentityId PeerId,
    string DisplayName,
    PeerInvitationPayload Invitation,
    DateTimeOffset InitiatedAtUtc,
    PublicIdentityId OwnerId = default);
