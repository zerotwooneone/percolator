using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.IdentityGate.Models;

/// <summary>
/// Presentation model representing an identity with a selection state in the Identity Gate.
/// </summary>
public partial class SelectableIdentityModel : ObservableObject
{
    public PublicIdentityId Id { get; }
    public string DisplayName { get; }
    public IdentityRole Role { get; }
    public IdentityState State { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string PublicKeyHex { get; }

    [ObservableProperty]
    private bool _isSelected;

    public SelectableIdentityModel(IdentityProfile profile, string publicKeyHex = "")
    {
        ArgumentNullException.ThrowIfNull(profile);
        Id = profile.Id;
        DisplayName = profile.DisplayName;
        Role = profile.Role;
        State = profile.State;
        CreatedAtUtc = profile.CreatedAtUtc;
        PublicKeyHex = publicKeyHex;
    }
}
