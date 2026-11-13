// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Observable persona row for exchange linking checkboxes — shared by the creation
/// wizard's optional linking step and the exchange manage surface (link/unlink is a
/// vault-local record; toggle state carries no wire semantics).
/// </summary>
public sealed partial class LinkablePersonaModel : ObservableObject
{
    private readonly Action<LinkablePersonaModel>? _notifyChanged;

    [ObservableProperty]
    private bool _isLinked;

    public LinkablePersonaModel(
        Guid personaId,
        string displayName,
        AvatarPresentation avatar,
        bool isLinked = false,
        Action<LinkablePersonaModel>? notifyChanged = null)
    {
        PersonaId = personaId;
        DisplayName = displayName ?? string.Empty;
        Avatar = avatar;
        _isLinked = isLinked;
        _notifyChanged = notifyChanged;
    }

    public Guid PersonaId { get; }
    public string DisplayName { get; }
    public AvatarPresentation Avatar { get; }

    partial void OnIsLinkedChanged(bool value) => _notifyChanged?.Invoke(this);
}
