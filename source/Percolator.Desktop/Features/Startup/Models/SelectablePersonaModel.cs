// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Desktop.Common.Services;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>Visual state of a persona card's port pill ([INV-DESKTOP-STEALTH-ACTIVATION-GATE]).</summary>
public enum PortStatus
{
    /// <summary>Preferred port is free — green pill.</summary>
    Ready,

    /// <summary>Preferred port taken, a replacement was found — orange pill.</summary>
    AutoSelected,

    /// <summary>No free port in the activation range — red pill, blocks launch.</summary>
    Unavailable
}

/// <summary>
/// Flat, immutable snapshot of a persona merged with its desktop launch settings —
/// the read projection consumed by the Identity Gate ([VM-006]).
/// </summary>
public sealed record PersonaDirectoryEntry(
    Guid IdentityId,
    string DisplayName,
    int PreferredPort,
    int? LastBoundPort,
    bool AutoStart,
    int SidebarOrder);

/// <summary>
/// A port lease plus the persona it belongs to, handed to the window handoff
/// coordinator at launch ([INV-DESKTOP-TWO-STAGE-HANDOFF]).
/// </summary>
public sealed record PersonaLaunchDescriptor(
    Guid IdentityId,
    string DisplayName,
    int RequestedPort,
    IPortLease Lease);

/// <summary>
/// Observable persona-card presentation model for Screen 2A: identity summary, selection
/// state, port probe outcome, and the derived avatar. Logic (probe, persist, launch)
/// stays in <c>IdentityGateViewModel</c> ([VM-013]); this model only carries bound state.
/// </summary>
public sealed partial class SelectablePersonaModel : ObservableObject
{
    private readonly Action<SelectablePersonaModel> _notifyChanged;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private int _preferredPort;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private PortStatus _portStatus = PortStatus.Ready;

    [ObservableProperty]
    private int _effectivePort;

    [ObservableProperty]
    private int _portEditorValue;

    [ObservableProperty]
    private bool _isPortEditorOpen;

    public SelectablePersonaModel(
        PersonaDirectoryEntry entry,
        IdenticonModel identicon,
        Action<SelectablePersonaModel>? notifyChanged = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(identicon);

        IdentityId = entry.IdentityId;
        _displayName = entry.DisplayName;
        _preferredPort = entry.PreferredPort;
        LastBoundPort = entry.LastBoundPort;
        AutoStart = entry.AutoStart;
        Avatar = AvatarPresentation.FromIdenticon(identicon);
        _effectivePort = entry.PreferredPort;
        _portEditorValue = entry.PreferredPort;
        _notifyChanged = notifyChanged ?? (_ => { });
    }

    public Guid IdentityId { get; }
    public int? LastBoundPort { get; }
    public bool AutoStart { get; }

    public AvatarPresentation Avatar { get; }

    /// <summary>True while a port conflict could not be resolved — disables launch.</summary>
    public bool HasUnresolvedConflict => PortStatus == PortStatus.Unavailable;

    /// <summary>Pill class selectors ([VIEW-008] declarative class toggling).</summary>
    public bool IsStatusReady => PortStatus == PortStatus.Ready;
    public bool IsStatusAutoSelected => PortStatus == PortStatus.AutoSelected;
    public bool IsStatusUnavailable => PortStatus == PortStatus.Unavailable;

    /// <summary>Pill caption, e.g. "Port 5100 Ready" or "Port 5100 in use → 5101".</summary>
    public string PortPillText => PortStatus switch
    {
        PortStatus.Ready => $"Port {EffectivePort} Ready",
        PortStatus.AutoSelected => $"Port {PreferredPort} in use → Auto-selected {EffectivePort}",
        _ => $"All default ports busy ({PreferredPort})"
    };

    partial void OnIsSelectedChanged(bool value) => _notifyChanged(this);

    partial void OnPortStatusChanged(PortStatus value)
    {
        OnPropertyChanged(nameof(PortPillText));
        OnPropertyChanged(nameof(HasUnresolvedConflict));
        OnPropertyChanged(nameof(IsStatusReady));
        OnPropertyChanged(nameof(IsStatusAutoSelected));
        OnPropertyChanged(nameof(IsStatusUnavailable));
        _notifyChanged(this);
    }

    partial void OnEffectivePortChanged(int value) => OnPropertyChanged(nameof(PortPillText));
}
