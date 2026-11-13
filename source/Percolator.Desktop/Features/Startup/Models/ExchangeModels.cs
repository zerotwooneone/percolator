// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using Percolator.Desktop.Common.Services;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Flat, immutable snapshot of an exchange (relay) merged with its linked persona ids —
/// the read projection consumed by the Identity Gate exchange tiles ([VM-006]).
/// </summary>
public sealed record ExchangeDirectoryEntry(
    Guid RelayIdentityId,
    string DisplayName,
    string Endpoint,
    int PreferredPort,
    bool IsSelfHosted,
    IReadOnlyList<Guid> LinkedPersonaIds);

/// <summary>
/// A port lease plus the self-hosted exchange it belongs to, handed to the window handoff
/// coordinator at launch alongside persona descriptors ([INV-DESKTOP-TWO-STAGE-HANDOFF]).
/// </summary>
public sealed record ExchangeLaunchDescriptor(
    Guid RelayIdentityId,
    string DisplayName,
    int RequestedPort,
    IPortLease Lease);

/// <summary>
/// Result returned by the exchange creation wizard. Linking is explicit opt-in: the wizard
/// ships with every persona checkbox unchecked ([INV-DESKTOP-PRESENTATION-MODELS]).
/// When <see cref="IsRemote"/> is set the result carries only <see cref="RelayIdentityId"/>
/// and <see cref="RemoteEndpoint"/> — remote exchanges have no local profile or port.
/// </summary>
public sealed record ExchangeCreationResult(
    PublicIdentityId RelayIdentityId,
    string DisplayName,
    int PreferredPort,
    IReadOnlyList<PublicIdentityId> LinkedPersonaIds,
    bool IsRemote = false,
    string? RemoteEndpoint = null);

/// <summary>Compact persona avatar chip shown stacked on an exchange tile's linkage strip.</summary>
public sealed record PersonaAvatarChip(
    Guid PersonaId,
    string DisplayName,
    AvatarPresentation Avatar);
