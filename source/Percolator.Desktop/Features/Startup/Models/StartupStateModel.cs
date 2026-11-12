// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>Active screen inside the unified 480×600 StartupWindow frame.</summary>
public enum StartupScreen
{
    /// <summary>Nothing shown yet; status determination in progress.</summary>
    None = 0,

    /// <summary>State 1A: first-time master password setup.</summary>
    VaultSetup = 1,

    /// <summary>State 1B: returning-user unlock prompt.</summary>
    VaultUnlock = 2,

    /// <summary>Screen 2: identity gate (persona selection).</summary>
    IdentityGate = 3
}

/// <summary>Terminal startup failure requiring a modal explanation.</summary>
public enum StartupErrorKind
{
    /// <summary>Database file/header exists but is unparseable or damaged.</summary>
    VaultDataDamaged,

    /// <summary>Native signal_shim cryptographic smoke test failed.</summary>
    SecurityEngineError
}

/// <summary>
/// Immutable snapshot of the startup flow projection ([INV-DESKTOP-PRESENTATION-MODELS]).
/// </summary>
public sealed record StartupStateModel(
    StartupScreen Screen,
    bool IsBusy,
    string? ErrorMessage);
