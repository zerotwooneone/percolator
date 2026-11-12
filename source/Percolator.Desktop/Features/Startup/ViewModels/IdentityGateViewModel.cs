// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for Screen 2 (Identity Gate). State 2A renders multi-select persona cards
/// with port-status pills, an inline port editor, and per-card context actions; State 2B
/// renders the empty-vault call-to-action. Launch acquires port leases and hands them to
/// <see cref="IWindowHandoffCoordinator"/> ([INV-DESKTOP-STEALTH-ACTIVATION-GATE],
/// [DEP-DESKTOP-PRE-ACTIVATION-STEALTH] — no sockets are held before launch executes).
/// </summary>
public partial class IdentityGateViewModel : ViewModelBase
{
    private readonly IPersonaDirectory _directory;
    private readonly IPortReservationService _ports;
    private readonly IDesktopSettingsRepository _settings;
    private readonly IIdenticonGenerator _identicons;
    private readonly IWindowHandoffCoordinator _handoff;
    private readonly IWindowManager _windows;
    private readonly ILogger<IdentityGateViewModel> _logger;

    private readonly ObservableList<SelectablePersonaModel> _personaList = new();
    private IAuthenticatedSession? _session;
    private bool _suppressAutoStartPersist;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchPersonasCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _bannerMessage;

    /// <summary>Bound to the single-persona auto-start checkbox; persisted via the R3 pipeline.</summary>
    [ObservableProperty]
    private bool _autoStartRequested;

    /// <summary>Synchronized projection of <c>_personaList</c> for XAML binding ([R3-008]).</summary>
    public INotifyCollectionChangedSynchronizedViewList<SelectablePersonaModel> Personas { get; }

    public IdentityGateViewModel(
        IPersonaDirectory directory,
        IPortReservationService ports,
        IDesktopSettingsRepository settings,
        IIdenticonGenerator identicons,
        IWindowHandoffCoordinator handoff,
        IWindowManager windows,
        ILogger<IdentityGateViewModel> logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _identicons = identicons ?? throw new ArgumentNullException(nameof(identicons));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var view = _personaList.CreateView(p => p);
        Personas = view.ToNotifyCollectionChanged();
        Bag.Add(view);
        Bag.Add(Personas);

        // Auto-start checkbox persistence: user toggles write through the settings store;
        // programmatic check-state changes during selection reload are suppressed. The
        // flag is read synchronously in the pipeline — SelectAwait defers work past
        // SetAutoStartCheckedSilently's finally and would see the flag already reset.
        this.ObservePropertyChanged(x => x.AutoStartRequested)
            .Where(_ => !_suppressAutoStartPersist)
            .SelectAwait(async (enabled, ct) =>
            {
                await PersistAutoStartAsync(enabled, ct).ConfigureAwait(true);
                return Unit.Default;
            }, AwaitOperation.Sequential)
            .Subscribe()
            .AddTo(ref Bag);
    }

    /// <summary>True while at least one persona exists — selects State 2A vs. 2B.</summary>
    public bool HasPersonas => _personaList.Count > 0;

    /// <summary>State 2B (empty vault) visibility.</summary>
    public bool HasEmptyState => !HasPersonas;

    /// <summary>Number of personas currently checked for launch.</summary>
    public int SelectedCount => _personaList.Count(p => p.IsSelected);

    /// <summary>The single selected persona, or <c>null</c> when zero or many are checked.</summary>
    public SelectablePersonaModel? SingleSelected =>
        SelectedCount == 1 ? _personaList.First(p => p.IsSelected) : null;

    /// <summary>Auto-start applies only to a single-persona selection.</summary>
    public bool IsAutoStartApplicable => SingleSelected is not null;

    public bool HasBanner => BannerMessage is not null;

    /// <summary>
    /// Populates the gate after unlock. Called by <c>StartupHostViewModel.NavigateToGateAsync</c>;
    /// <paramref name="session"/> is <c>null</c> on the duress decoy path.
    /// </summary>
    public async Task LoadAsync(IAuthenticatedSession? session, CancellationToken ct = default)
    {
        _session = session;
        IsLoading = true;
        try
        {
            var entries = await _directory.ListAsync(session, ct).ConfigureAwait(true);
            _personaList.Clear();
            foreach (var entry in entries)
            {
                var model = BuildModel(entry);
                _personaList.Add(model);
                await ProbeCardAsync(model, ct).ConfigureAwait(true);
            }

            RefreshDerivedState();
            SetAutoStartCheckedSilently(SingleSelected?.AutoStart ?? false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load personas for the Identity Gate.");
            BannerMessage = "Could not load personas.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>[ Launch Selected Personas → ] — acquires leases, then swaps to the shell.</summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchPersonasAsync(CancellationToken ct)
    {
        IsBusy = true;
        var descriptors = new List<PersonaLaunchDescriptor>();
        try
        {
            foreach (var persona in _personaList.Where(p => p.IsSelected).ToList())
            {
                var lease = await _ports.AcquireAsync(persona.EffectivePort, ct).ConfigureAwait(true);
                if (lease is null)
                {
                    persona.PortStatus = PortStatus.Unavailable;
                    BannerMessage = $"All default ports are busy; {persona.DisplayName} cannot launch.";
                    DisposeLeases(descriptors);
                    return;
                }

                if (lease.Port != persona.EffectivePort)
                {
                    // Race recovery: another process claimed the port between selection
                    // and launch — re-lease the next free port and surface the banner.
                    persona.EffectivePort = lease.Port;
                    persona.PortStatus = PortStatus.AutoSelected;
                    BannerMessage =
                        $"Port {persona.PreferredPort} was claimed by another application. " +
                        $"Re-leased port {lease.Port} for {persona.DisplayName}.";
                }

                descriptors.Add(new PersonaLaunchDescriptor(
                    persona.IdentityId, persona.DisplayName, lease.Port, lease));
                await PersistBoundPortAsync(persona.IdentityId, lease.Port, ct).ConfigureAwait(true);
            }

            await _handoff.TransitionToMainShellAsync(_session, descriptors, ct).ConfigureAwait(true);
        }
        catch
        {
            // Handoff owns leases only after a successful transition — on failure, release
            // them here so no bound socket lingers ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
            DisposeLeases(descriptors);
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>[ Create Your First Persona ] / card-adjacent create affordance.</summary>
    [RelayCommand]
    private async Task CreatePersonaAsync(CancellationToken ct)
    {
        var result = await _windows
            .ShowDialogAsync<CreatePersonaDialogViewModel, PersonaEditorResult?>(
                vm => vm.InitializeForCreate())
            .ConfigureAwait(true);
        if (result?.IdentityId is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var entry = await _directory
                .CreateAsync(_session, result.IdentityId.Value, result.DisplayName, result.PreferredPort, ct)
                .ConfigureAwait(true);
            var model = BuildModel(entry);
            model.IsSelected = true;
            _personaList.Add(model);
            await ProbeCardAsync(model, ct).ConfigureAwait(true);
            RefreshDerivedState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Card context menu: Edit Profile — reuses the editor dialog in edit mode.</summary>
    [RelayCommand]
    private async Task EditPersonaAsync(SelectablePersonaModel? persona, CancellationToken ct)
    {
        if (persona is null)
        {
            return;
        }

        var result = await _windows
            .ShowDialogAsync<CreatePersonaDialogViewModel, PersonaEditorResult?>(
                vm => vm.InitializeForEdit(persona.DisplayName, persona.PreferredPort, persona.IdentityId))
            .ConfigureAwait(true);
        if (result is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _directory
                .RenameAsync(_session, new PublicIdentityId(persona.IdentityId), result.DisplayName, ct)
                .ConfigureAwait(true);
            await PersistPortPreferenceAsync(persona.IdentityId, result.PreferredPort, ct).ConfigureAwait(true);

            persona.DisplayName = result.DisplayName;
            persona.PreferredPort = result.PreferredPort;
            await ProbeCardAsync(persona, ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Card context menu: Delete Persona — type-to-confirm shred dialog.</summary>
    [RelayCommand]
    private async Task DeletePersonaAsync(SelectablePersonaModel? persona, CancellationToken ct)
    {
        if (persona is null)
        {
            return;
        }

        var confirmed = await _windows
            .ShowDialogAsync<DeletePersonaDialogViewModel, bool>(
                vm => vm.Initialize(persona.DisplayName))
            .ConfigureAwait(true);
        if (confirmed != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _directory
                .ShredAndDeleteAsync(_session, new PublicIdentityId(persona.IdentityId), ct)
                .ConfigureAwait(true);
            _personaList.Remove(persona);
            RefreshDerivedState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies the port popover value: persists the preference and re-probes.</summary>
    [RelayCommand]
    private async Task ApplyPortOverrideAsync(SelectablePersonaModel? persona, CancellationToken ct)
    {
        if (persona is null)
        {
            return;
        }

        persona.IsPortEditorOpen = false;
        persona.PreferredPort = persona.PortEditorValue;
        await PersistPortPreferenceAsync(persona.IdentityId, persona.PreferredPort, ct).ConfigureAwait(true);
        await ProbeCardAsync(persona, ct).ConfigureAwait(true);
    }

    /// <summary>Clears the inline conflict/recovery banner.</summary>
    [RelayCommand]
    private void DismissBanner() => BannerMessage = null;

    private bool CanLaunch() =>
        !IsBusy
        && !_personaList.Any(p => p.IsSelected && p.HasUnresolvedConflict)
        && _personaList.Any(p => p.IsSelected);

    private SelectablePersonaModel BuildModel(PersonaDirectoryEntry entry) =>
        new(entry, _identicons.Generate(entry.IdentityId), _ => OnPersonaStateChanged());

    private void OnPersonaStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SingleSelected));
        OnPropertyChanged(nameof(IsAutoStartApplicable));
        SetAutoStartCheckedSilently(SingleSelected?.AutoStart ?? false);
        LaunchPersonasCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDerivedState()
    {
        OnPropertyChanged(nameof(HasPersonas));
        OnPropertyChanged(nameof(HasEmptyState));
        OnPersonaStateChanged();
    }

    private async Task ProbeCardAsync(SelectablePersonaModel persona, CancellationToken ct)
    {
        var probe = await _ports.ProbeAsync(persona.PreferredPort, ct).ConfigureAwait(true);
        switch (probe.Status)
        {
            case PortProbeStatus.Ready:
                persona.EffectivePort = persona.PreferredPort;
                persona.PortStatus = PortStatus.Ready;
                break;
            case PortProbeStatus.AutoSelected:
                persona.EffectivePort = probe.Port!.Value;
                persona.PortStatus = PortStatus.AutoSelected;
                break;
            default:
                persona.PortStatus = PortStatus.Unavailable;
                break;
        }
    }

    private async Task PersistPortPreferenceAsync(Guid identityId, int port, CancellationToken ct)
    {
        var entity = await _settings.GetAsync(identityId, ct).ConfigureAwait(false)
            ?? new DesktopIdentitySettingsEntity(identityId, port, null, false, 0);
        await _settings.SaveAsync(entity with { PreferredPort = port }, ct).ConfigureAwait(false);
    }

    private async Task PersistBoundPortAsync(Guid identityId, int port, CancellationToken ct)
    {
        var entity = await _settings.GetAsync(identityId, ct).ConfigureAwait(false)
            ?? new DesktopIdentitySettingsEntity(identityId, port, port, false, 0);
        await _settings.SaveAsync(entity with { LastBoundPort = port }, ct).ConfigureAwait(false);
    }

    private async Task PersistAutoStartAsync(bool enabled, CancellationToken ct)
    {
        var target = SingleSelected;
        if (target is null)
        {
            return;
        }

        var all = await _settings.GetAllAsync(ct).ConfigureAwait(false);
        var hasTarget = false;
        foreach (var entity in all)
        {
            var desired = entity.IdentityId == target.IdentityId && enabled;
            hasTarget |= entity.IdentityId == target.IdentityId;
            if (entity.AutoStart != desired)
            {
                await _settings.SaveAsync(entity with { AutoStart = desired }, ct).ConfigureAwait(false);
            }
        }

        if (!hasTarget)
        {
            await _settings.SaveAsync(
                new DesktopIdentitySettingsEntity(target.IdentityId, target.PreferredPort, null, enabled, all.Count),
                ct).ConfigureAwait(false);
        }
    }

    private void SetAutoStartCheckedSilently(bool value)
    {
        _suppressAutoStartPersist = true;
        try
        {
            AutoStartRequested = value;
        }
        finally
        {
            _suppressAutoStartPersist = false;
        }
    }

    private static void DisposeLeases(IEnumerable<PersonaLaunchDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            descriptor.Lease.Dispose();
        }
    }

    partial void OnBannerMessageChanged(string? value) => OnPropertyChanged(nameof(HasBanner));
}
