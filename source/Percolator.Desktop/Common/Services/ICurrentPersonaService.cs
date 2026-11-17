// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System.Collections.Generic;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Session-scoped presentation service managing the currently focused persona and
/// the collection of active, launched personas in the desktop shell
/// (INV-DESKTOP-STATEFUL-PROJECTIONS, DEP-DESKTOP-PERSONA-ISOLATION).
/// </summary>
public interface ICurrentPersonaService
{
    /// <summary>Snapshot of the currently focused persona, or null if none is selected.</summary>
    ActivePersonaModel? CurrentPersona { get; }

    /// <summary>Immutable snapshot of all personas unlocked and launched in the current session.</summary>
    IReadOnlyList<ActivePersonaModel> ActivePersonas { get; }

    /// <summary>Reactive stream that emits whenever the active persona selection changes.</summary>
    Observable<ActivePersonaModel?> CurrentPersonaChanged { get; }

    /// <summary>Seeds the active personas launched during session handoff.</summary>
    void InitializeActivePersonas(IEnumerable<ActivePersonaModel> personas);

    /// <summary>Switches the currently active persona and notifies all observers.</summary>
    void SelectPersona(ActivePersonaModel persona);

    /// <summary>Switches the active persona by ID from the active session personas.</summary>
    bool SelectPersonaById(PublicIdentityId personaId);
}
