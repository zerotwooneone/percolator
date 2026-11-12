// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for Screen 2 (Identity Gate). DESK-02 ships only the empty-vault state (2B)
/// so the duress decoy transition has a landing screen; persona selection, port binding,
/// and launch arrive in DESK-03.
/// </summary>
public partial class IdentityGateViewModel : ViewModelBase
{
    // DESK-03: SelectablePersonaModel collection, port status, auto-start, launch command.
}
