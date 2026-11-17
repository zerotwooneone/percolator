// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using Percolator.Domain.Identities;

namespace Percolator.Desktop.Features.Shell.Models;

/// <summary>
/// Immutable presentation snapshot of the currently active desktop persona
/// (INV-DESKTOP-PRESENTATION-MODELS, VM-006).
/// </summary>
public sealed record ActivePersonaModel(
    PublicIdentityId IdentityId,
    string DisplayName,
    int BoundPort);
