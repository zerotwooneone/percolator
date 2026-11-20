// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// Observable exchange-card presentation model for the Identity Gate's Exchanges group:
/// multi-select-for-launch state, self-hosted badge/accent state, port probe outcome, and the
/// stacked-avatar linkage strip capped at five chips with a `+N` overflow count. Logic stays
/// in <see cref="IdentityGateViewModel"/> ([VM-013]); this model only carries bound state.
/// </summary>
public sealed partial class ExchangeTileViewModel : ViewModelBase
{
    /// <summary>Maximum persona avatars rendered on the card-stack linkage strip.</summary>
    public const int MaxVisibleLinkedPersonas = 5;

    private readonly Action<ExchangeTileViewModel> _notifyChanged;

    [ObservableProperty]
    private bool _isSelectedForLaunch;

    [ObservableProperty]
    private PortStatus _portStatus = PortStatus.Ready;

    [ObservableProperty]
    private int _effectivePort;

    /// <summary>Tile caption — settable so manage-surface profile edits refresh in place.</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>Launch port preference — updated when the profile edit saves a new port.</summary>
    [ObservableProperty]
    private int _preferredPort;

    /// <summary>The persona avatars currently rendered on the linkage strip (≤ 5).</summary>
    public IReadOnlyList<PersonaAvatarChip> VisibleLinkedPersonas { get; private set; } =
        Array.Empty<PersonaAvatarChip>();

    public ExchangeTileViewModel(
        ExchangeDirectoryEntry entry,
        IReadOnlyList<PersonaAvatarChip> linkedPersonaChips,
        AvatarPresentation avatar,
        Action<ExchangeTileViewModel>? notifyChanged = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(linkedPersonaChips);
        ArgumentNullException.ThrowIfNull(avatar);

        RelayIdentityId = entry.RelayIdentityId;
        _displayName = entry.DisplayName;
        Endpoint = entry.Endpoint;
        _preferredPort = entry.PreferredPort;
        IsSelfHosted = entry.IsSelfHosted;
        LinkedPersonaIds = entry.LinkedPersonaIds;
        Avatar = avatar;
        _effectivePort = entry.PreferredPort;
        _notifyChanged = notifyChanged ?? (_ => { });
        SetLinkedPersonas(linkedPersonaChips);
    }

    /// <summary>Deterministic identicon derived from the relay identity.</summary>
    public AvatarPresentation Avatar { get; }

    public Guid RelayIdentityId { get; }
    public string Endpoint { get; }

    /// <summary>Self-created exchanges get the badge + accent treatment ([INV-DESKTOP-PRESENTATION-MODELS]).</summary>
    public bool IsSelfHosted { get; }

    /// <summary>All persona ids currently linked to this exchange (membership, no ordering).</summary>
    public IReadOnlyList<Guid> LinkedPersonaIds { get; private set; }

    /// <summary>Linked personas beyond the five rendered chips, surfaced as the `+N` chip.</summary>
    public int OverflowLinkedPersonaCount { get; private set; }

    /// <summary>True while at least one persona chip renders on the strip.</summary>
    public bool HasLinkedPersonas => VisibleLinkedPersonas.Count > 0;

    /// <summary>True while hidden linked personas remain beyond the five rendered chips.</summary>
    public bool HasLinkedOverflow => OverflowLinkedPersonaCount > 0;

    /// <summary>True while a port conflict could not be resolved — blocks launch.</summary>
    public bool HasUnresolvedConflict => PortStatus == PortStatus.Unavailable;

    public bool IsStatusReady => PortStatus == PortStatus.Ready;
    public bool IsStatusAutoSelected => PortStatus == PortStatus.AutoSelected;
    public bool IsStatusUnavailable => PortStatus == PortStatus.Unavailable;

    /// <summary>Pill caption — same text contract as persona cards.</summary>
    public string PortPillText => PortStatus switch
    {
        PortStatus.Ready => $"Port {EffectivePort} Ready",
        PortStatus.AutoSelected => $"Port {PreferredPort} in use → Auto-selected {EffectivePort}",
        _ => $"All default ports busy ({PreferredPort})"
    };

    /// <summary>Refreshes the linkage strip after link/unlink edits in the manage surface.</summary>
    public void SetLinkedPersonas(IReadOnlyList<PersonaAvatarChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);

        VisibleLinkedPersonas = chips.Count <= MaxVisibleLinkedPersonas
            ? chips
            : chips.Take(MaxVisibleLinkedPersonas).ToList();
        OverflowLinkedPersonaCount = Math.Max(0, chips.Count - MaxVisibleLinkedPersonas);
        OnPropertyChanged(nameof(VisibleLinkedPersonas));
        OnPropertyChanged(nameof(OverflowLinkedPersonaCount));
        OnPropertyChanged(nameof(HasLinkedPersonas));
        OnPropertyChanged(nameof(HasLinkedOverflow));
    }

    /// <summary>Refreshes link membership after a manage-surface edit.</summary>
    public void SetLinkedPersonaIds(IReadOnlyList<Guid> personaIds)
    {
        ArgumentNullException.ThrowIfNull(personaIds);
        LinkedPersonaIds = personaIds;
        OnPropertyChanged(nameof(LinkedPersonaIds));
    }

    partial void OnIsSelectedForLaunchChanged(bool value) => _notifyChanged(this);

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

    partial void OnPreferredPortChanged(int value) => OnPropertyChanged(nameof(PortPillText));
}
