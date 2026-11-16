// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using Percolator.Domain.Identities;

namespace Percolator.Desktop.Features.Transports.Models;

/// <summary>
/// Presentation model representing an unsolicited inbound contact request or handshake offer
/// quarantined in the pending requests queue awaiting explicit user consent.
/// </summary>
public sealed record InboundPendingRequestModel(
    PublicIdentityId RequesterId,
    string DisplayName,
    string FingerprintPreview,
    DateTimeOffset ReceivedAtUtc);
