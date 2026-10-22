using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.IdentityGate.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.IdentityGate.ViewModels;

public partial class IdentityGateViewModel : ViewModelBase
{
    private readonly IIdentityProfileRepository _profileRepository;
    private readonly IDesktopIdentityKeyStore _keyStore;
    private readonly INetworkStealthGate _stealthGate;
    private readonly IWindowManager _windowManager;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<IdentityGateViewModel> _logger;

    public ObservableCollection<SelectableIdentityModel> Identities { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateSessionCommand))]
    private bool _canActivate;

    [ObservableProperty]
    private bool _isCreateDialogOpen;

    [ObservableProperty]
    private string _newDisplayName = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _vaultPassphrase = string.Empty;

    [ObservableProperty]
    private bool _isVaultLocked;

    public IdentityGateViewModel(
        IIdentityProfileRepository profileRepository,
        IDesktopIdentityKeyStore keyStore,
        INetworkStealthGate stealthGate,
        IWindowManager windowManager,
        IDateTimeProvider dateTimeProvider,
        ILogger<IdentityGateViewModel> logger)
    {
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _stealthGate = stealthGate ?? throw new ArgumentNullException(nameof(stealthGate));
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ = InitializeAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            var existing = (await _profileRepository.GetAllAsync().ConfigureAwait(true))
                .OrderBy(p => p.CreatedAtUtc)
                .ThenBy(p => p.DisplayName)
                .ToList();

            Identities.Clear();

            foreach (var profile in existing)
            {
                var pubKey = await _keyStore.GetIdentityPublicKeyAsync(profile.Id).ConfigureAwait(true);
                var hex = pubKey != null ? Convert.ToHexString(pubKey.Span) : string.Empty;
                var model = new SelectableIdentityModel(profile, hex);
                model.PropertyChanged += OnIdentityModelPropertyChanged;
                Identities.Add(model);
            }

            // Auto-select the first identity if available
            if (Identities.Count > 0)
            {
                Identities[0].IsSelected = true;
            }

            UpdateCanActivate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize IdentityGateViewModel identities.");
            ErrorMessage = "Failed to load local identities.";
        }
    }

    private void OnIdentityModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableIdentityModel.IsSelected))
        {
            UpdateCanActivate();
        }
    }

    private void UpdateCanActivate()
    {
        CanActivate = Identities.Any(i => i.IsSelected);
    }

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateSessionAsync()
    {
        var selected = Identities.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "Please select at least one identity to activate.";
            return;
        }

        ErrorMessage = null;
        _logger.LogInformation("Activating session with {Count} selected identities.", selected.Count);

        try
        {
            await _stealthGate.ActivateAsync(selected).ConfigureAwait(true);
            _windowManager.SwitchToMainWindow();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating network stealth gate.");
            ErrorMessage = $"Activation failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenCreateIdentityDialog()
    {
        NewDisplayName = string.Empty;
        ErrorMessage = null;
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    private void CancelCreateIdentity()
    {
        IsCreateDialogOpen = false;
        NewDisplayName = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmCreateIdentityAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDisplayName))
        {
            ErrorMessage = "Identity display name cannot be empty.";
            return;
        }

        var name = NewDisplayName.Trim();
        var id = PublicIdentityId.New();

        // Generate Ed25519 identity key bytes
        var privKey = new byte[32];
        var pubKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(privKey);
        RandomNumberGenerator.Fill(pubKeyBytes);

        var pubKey = IdentityKey.FromSpan(pubKeyBytes);
        await _keyStore.SaveKeyAsync(id, privKey, pubKey).ConfigureAwait(true);

        var profileResult = IdentityProfile.Create(id, name, IdentityRole.UserPersona, _dateTimeProvider);
        if (profileResult.IsFailure)
        {
            ErrorMessage = profileResult.Error.Description;
            return;
        }

        var profile = profileResult.Value!;
        await _profileRepository.SaveAsync(profile).ConfigureAwait(true);

        var hex = Convert.ToHexString(pubKey.Span);
        var model = new SelectableIdentityModel(profile, hex)
        {
            IsSelected = true
        };
        model.PropertyChanged += OnIdentityModelPropertyChanged;
        Identities.Add(model);

        IsCreateDialogOpen = false;
        NewDisplayName = string.Empty;
        ErrorMessage = null;

        UpdateCanActivate();
        _logger.LogInformation("Created and selected new identity '{Name}' ({Id})", name, id);
    }

    [RelayCommand]
    private void UnlockVault()
    {
        if (string.IsNullOrWhiteSpace(VaultPassphrase))
        {
            ErrorMessage = "Passphrase cannot be empty.";
            return;
        }

        IsVaultLocked = false;
        VaultPassphrase = string.Empty;
        ErrorMessage = null;
    }
}
