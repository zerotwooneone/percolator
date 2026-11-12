using System;

namespace Percolator.Desktop.Common.Storage;

/// <summary>
/// Pre-activation per-persona UI settings persisted in the unencrypted desktop settings store.
/// Identity keys and profiles stay in the encrypted vault — this row only holds presentation
/// and launch preferences keyed by persona id ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
/// </summary>
public sealed record DesktopIdentitySettingsEntity(
    Guid IdentityId,
    int PreferredPort,
    int? LastBoundPort,
    bool AutoStart,
    int SidebarOrder);
