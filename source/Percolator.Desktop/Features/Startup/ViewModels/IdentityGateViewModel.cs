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
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;
using R3;
using IPortReservationService = Percolator.Desktop.Common.Services.IPortReservationService;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

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
    private readonly IExchangeDirectory _exchanges;
    private readonly IPortReservationService _ports;
    private readonly IDesktopSettingsRepository _settings;
    private readonly IIdenticonGenerator _identicons;
    private readonly IWindowHandoffCoordinator _handoff;
    private readonly IWindowManager _windows;
    private readonly ILogger<IdentityGateViewModel> _logger;

    private readonly ObservableList<SelectablePersonaModel> _personaList = new();
    private readonly ObservableList<ExchangeTileViewModel> _exchangeList = new();

    // Assigned by LoadAsync before the VM is shown — the gate only exists post-unlock and
    // the session is always real (decoy-init failure surfaces as a generic unlock failure).
    private IAuthenticatedSession _session = null!;
    private bool _suppressAutoStartPersist;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExchangeCreateEnabled))]
    [NotifyCanExecuteChangedFor(nameof(LaunchPersonasCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateExchangeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _bannerMessage;

    /// <summary>Bound to the single-persona auto-start checkbox; persisted via the R3 pipeline.</summary>
    [ObservableProperty]
    private bool _autoStartRequested;

    /// <summary>
    /// True while the first-run onboarding gate is active — the hollow `+` Exchange tile
    /// renders ghosted and inert until setup finishes (first launch) or the right-click
    /// "Disable onboarding" escape hatch runs.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExchangeCreateEnabled))]
    [NotifyPropertyChangedFor(nameof(ExchangeCreateTooltip))]
    [NotifyCanExecuteChangedFor(nameof(CreateExchangeCommand))]
    private bool _isOnboardingActive;

    /// <summary>Synchronized projection of <c>_personaList</c> for XAML binding ([R3-008]).</summary>
    public INotifyCollectionChangedSynchronizedViewList<SelectablePersonaModel> Personas { get; }

    /// <summary>Synchronized projection of <c>_exchangeList</c> — the Exchanges group below Personas.</summary>
    public INotifyCollectionChangedSynchronizedViewList<ExchangeTileViewModel> Exchanges { get; }

    public IdentityGateViewModel(
        IPersonaDirectory directory,
        IExchangeDirectory exchanges,
        IPortReservationService ports,
        IDesktopSettingsRepository settings,
        IIdenticonGenerator identicons,
        IWindowHandoffCoordinator handoff,
        IWindowManager windows,
        ILogger<IdentityGateViewModel> logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
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

        var exchangeView = _exchangeList.CreateView(e => e);
        Exchanges = exchangeView.ToNotifyCollectionChanged();
        Bag.Add(exchangeView);
        Bag.Add(Exchanges);

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

    /// <summary>True while at least one exchange exists — the group collapses to a `+` tile otherwise.</summary>
    public bool HasExchanges => _exchangeList.Count > 0;

    /// <summary>Number of exchanges currently checked for launch.</summary>
    public int SelectedExchangeCount => _exchangeList.Count(e => e.IsSelectedForLaunch);

    /// <summary>The `+` Exchange tile stays inert while the onboarding gate is active.</summary>
    public bool IsExchangeCreateEnabled => !IsOnboardingActive && !IsBusy;

    /// <summary>`+` tile affordance text — "finish setup" affordance while onboarding is active.</summary>
    public string ExchangeCreateTooltip =>
        IsOnboardingActive ? "Finish setup to unlock" : "Run your own post-office";

    /// <summary>The single selected persona, or <c>null</c> when zero or many are checked.</summary>
    public SelectablePersonaModel? SingleSelected =>
        SelectedCount == 1 ? _personaList.First(p => p.IsSelected) : null;

    /// <summary>Auto-start applies only to a single-persona selection.</summary>
    public bool IsAutoStartApplicable => SingleSelected is not null;

    public bool HasBanner => BannerMessage is not null;

    /// <summary>
    /// Populates the gate after unlock. Called by <c>StartupHostViewModel.NavigateToGateAsync</c>
    /// with the authenticated session — always non-null, including the duress decoy.
    /// </summary>
    public async Task LoadAsync(IAuthenticatedSession session, CancellationToken ct = default)
    {
        _session = session;
        IsLoading = true;
        try
        {
            var entries = await _directory.ListAsync(ct).ConfigureAwait(true);
            _personaList.Clear();
            foreach (var entry in entries)
            {
                var model = BuildModel(entry);
                _personaList.Add(model);
                await ProbeCardAsync(model, ct).ConfigureAwait(true);
            }

            // The Exchange `+` tile stays ghosted until the first successful launch marks
            // setup complete or the context-menu escape hatch disables onboarding.
            var onboardingFlag = await _settings
                .GetAppSettingAsync(VaultSettingKeys.OnboardingCompleted, ct)
                .ConfigureAwait(true);
            IsOnboardingActive = onboardingFlag != VaultSettingKeys.FlagSetValue;

            _exchangeList.Clear();
            var exchangeEntries = await _exchanges.ListAsync(ct).ConfigureAwait(true);
            foreach (var exchangeEntry in exchangeEntries)
            {
                var tile = BuildExchangeTile(exchangeEntry);
                _exchangeList.Add(tile);
                await ProbeExchangeAsync(tile, ct).ConfigureAwait(true);
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

    /// <summary>[ Launch Selected → ] — acquires leases, starts selected self-hosted exchange
    /// endpoints, provisions linked mailboxes, then swaps to the shell.</summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchPersonasAsync(CancellationToken ct)
    {
        IsBusy = true;
        var descriptors = new List<PersonaLaunchDescriptor>();
        var exchangeDescriptors = new List<ExchangeLaunchDescriptor>();
        try
        {
            foreach (var persona in _personaList.Where(p => p.IsSelected).ToList())
            {
                var lease = await _ports.AcquireAsync(persona.EffectivePort, ct).ConfigureAwait(true);
                if (lease is null)
                {
                    persona.PortStatus = PortStatus.Unavailable;
                    BannerMessage = $"All default ports are busy; {persona.DisplayName} cannot launch.";
                    DisposeLeases(descriptors, exchangeDescriptors);
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

            foreach (var exchange in _exchangeList
                         .Where(e => e.IsSelectedForLaunch && e.IsSelfHosted).ToList())
            {
                var lease = await _ports.AcquireAsync(exchange.EffectivePort, ct).ConfigureAwait(true);
                if (lease is null)
                {
                    exchange.PortStatus = PortStatus.Unavailable;
                    BannerMessage = $"All default ports are busy; {exchange.DisplayName} cannot launch.";
                    DisposeLeases(descriptors, exchangeDescriptors);
                    return;
                }

                if (lease.Port != exchange.EffectivePort)
                {
                    exchange.EffectivePort = lease.Port;
                    exchange.PortStatus = PortStatus.AutoSelected;
                    BannerMessage =
                        $"Port {exchange.PreferredPort} was claimed by another application. " +
                        $"Re-leased port {lease.Port} for {exchange.DisplayName}.";
                }

                exchangeDescriptors.Add(new ExchangeLaunchDescriptor(
                    exchange.RelayIdentityId, exchange.DisplayName, lease.Port, lease));
                await PersistBoundPortAsync(exchange.RelayIdentityId, lease.Port, ct).ConfigureAwait(true);
            }

            if (exchangeDescriptors.Count > 0)
            {
                // Relay endpoints must be mapped before the handoff starts the session host —
                // the hosted transport no-ops once running, so the first StartAsync wins.
                foreach (var descriptor in exchangeDescriptors)
                {
                    try
                    {
                        await _exchanges
                            .StartSelfHostedEndpointAsync(descriptor, ct)
                            .ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start relay endpoints for {Exchange}.", descriptor.DisplayName);
                        BannerMessage = $"{descriptor.DisplayName} could not start its relay endpoints.";
                    }
                }
            }

            var selectedPersonaIds = descriptors
                .Select(d => new PublicIdentityId(d.IdentityId))
                .ToList();
            if (selectedPersonaIds.Count > 0)
            {
                // Best effort: unreachable exchanges surface as a banner; mailboxes
                // re-provision on the next launch.
                var provision = await _exchanges
                    .ProvisionLinkedMailboxesAsync(selectedPersonaIds, ct)
                    .ConfigureAwait(true);
                if (provision.IsFailure)
                {
                    _logger.LogWarning("Mailbox provisioning incomplete: {Error}", provision.Error.Description);
                    BannerMessage = "Some mailboxes could not be provisioned; they will retry on the next launch.";
                }
            }

            await _handoff
                .TransitionToMainShellAsync(_session, descriptors, exchangeDescriptors, ct)
                .ConfigureAwait(true);

            // First successful launch finishes onboarding — the `+` Exchange tile unlocks
            // on every subsequent gate visit.
            await _settings
                .SetAppSettingAsync(VaultSettingKeys.OnboardingCompleted, VaultSettingKeys.FlagSetValue, ct)
                .ConfigureAwait(true);
            IsOnboardingActive = false;
        }
        catch
        {
            // Handoff owns leases only after a successful transition — on failure, release
            // them here so no bound socket lingers ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
            DisposeLeases(descriptors, exchangeDescriptors);
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
                .CreateAsync(result.IdentityId.Value, result.DisplayName, result.PreferredPort, ct)
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
                .RenameAsync(new PublicIdentityId(persona.IdentityId), result.DisplayName, ct)
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
                .ShredAndDeleteAsync(new PublicIdentityId(persona.IdentityId), ct)
                .ConfigureAwait(true);
            _personaList.Remove(persona);
            RefreshDerivedState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Hollow `+` tile — opens the three-step exchange creation wizard.</summary>
    [RelayCommand(CanExecute = nameof(IsExchangeCreateEnabled))]
    private async Task CreateExchangeAsync(CancellationToken ct)
    {
        var personas = _personaList.Select(ToPersonaEntry).ToList();
        var result = await _windows
            .ShowDialogAsync<ExchangeCreationViewModel, ExchangeCreationResult?>(
                vm => vm.Initialize(personas))
            .ConfigureAwait(true);
        if (result is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var entry = await _exchanges
                .CreateSelfHostedAsync(
                    result.RelayIdentityId,
                    result.DisplayName,
                    result.PreferredPort,
                    result.LinkedPersonaIds,
                    ct)
                .ConfigureAwait(true);

            // The tile appears offline and inert — nothing listens until launch selects it.
            var tile = BuildExchangeTile(entry);
            _exchangeList.Add(tile);
            await ProbeExchangeAsync(tile, ct).ConfigureAwait(true);
            RefreshDerivedState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create the exchange.");
            BannerMessage = "The exchange could not be saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Tile hover gear — opens the link/unlink manage surface for the exchange.</summary>
    [RelayCommand]
    private async Task ManageExchangeAsync(ExchangeTileViewModel? exchange, CancellationToken ct)
    {
        if (exchange is null)
        {
            return;
        }

        var personas = _personaList.Select(ToPersonaEntry).ToList();
        var changed = await _windows
            .ShowDialogAsync<ExchangeManageViewModel, bool>(
                vm => vm.Initialize(exchange, personas))
            .ConfigureAwait(true);
        if (changed != true)
        {
            return;
        }

        var fresh = await _exchanges.ListAsync(ct).ConfigureAwait(true);
        var entry = fresh.FirstOrDefault(e => e.RelayIdentityId == exchange.RelayIdentityId);
        if (entry is null)
        {
            _exchangeList.Remove(exchange);
            RefreshDerivedState();
            return;
        }

        exchange.SetLinkedPersonaIds(entry.LinkedPersonaIds);
        exchange.SetLinkedPersonas(BuildChips(entry.LinkedPersonaIds));
        exchange.DisplayName = entry.DisplayName;
        if (exchange.IsSelfHosted && entry.PreferredPort != exchange.PreferredPort)
        {
            // Profile edit may have changed the launch port — re-probe the conflict state.
            exchange.PreferredPort = entry.PreferredPort;
            await ProbeExchangeAsync(exchange, ct).ConfigureAwait(true);
        }
    }

    /// <summary>Gate context-menu escape hatch — intentionally undiscoverable (right-click only).</summary>
    [RelayCommand]
    private async Task DisableOnboardingAsync(CancellationToken ct)
    {
        await _settings
            .SetAppSettingAsync(VaultSettingKeys.OnboardingCompleted, VaultSettingKeys.FlagSetValue, ct)
            .ConfigureAwait(true);
        IsOnboardingActive = false;
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
        && !_exchangeList.Any(e => e.IsSelectedForLaunch && e.HasUnresolvedConflict)
        && (_personaList.Any(p => p.IsSelected) || _exchangeList.Any(e => e.IsSelectedForLaunch));

    private SelectablePersonaModel BuildModel(PersonaDirectoryEntry entry) =>
        new(entry, _identicons.Generate(entry.IdentityId), _ => OnPersonaStateChanged());

    private ExchangeTileViewModel BuildExchangeTile(ExchangeDirectoryEntry entry) =>
        new(entry,
            BuildChips(entry.LinkedPersonaIds),
            AvatarPresentation.FromIdenticon(_identicons.Generate(entry.RelayIdentityId)),
            _ => OnExchangeStateChanged());

    private IReadOnlyList<PersonaAvatarChip> BuildChips(IReadOnlyList<Guid> linkedIds) =>
        linkedIds
            .Select(id => new PersonaAvatarChip(
                id,
                _personaList.FirstOrDefault(p => p.IdentityId == id)?.DisplayName
                    ?? id.ToString("N")[..8],
                AvatarPresentation.FromIdenticon(_identicons.Generate(id))))
            .ToList();

    private static PersonaDirectoryEntry ToPersonaEntry(SelectablePersonaModel model) =>
        new(model.IdentityId, model.DisplayName, model.PreferredPort, model.LastBoundPort, model.AutoStart, 0);

    private void OnExchangeStateChanged()
    {
        OnPropertyChanged(nameof(SelectedExchangeCount));
        LaunchPersonasCommand.NotifyCanExecuteChanged();
    }

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
        OnPropertyChanged(nameof(HasExchanges));
        // _session is a plain field — its assignment in LoadAsync raises no notification,
        // and IsOnboardingActive/IsBusy only notify when their value actually changes. A
        // vault with onboarding already complete would otherwise leave the `+` tile bound
        // to a stale IsEnabled=false forever.
        OnPropertyChanged(nameof(IsExchangeCreateEnabled));
        OnPropertyChanged(nameof(ExchangeCreateTooltip));
        CreateExchangeCommand.NotifyCanExecuteChanged();
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

    /// <summary>Only self-hosted exchanges bind local ports — remote tiles skip the probe.</summary>
    private async Task ProbeExchangeAsync(ExchangeTileViewModel exchange, CancellationToken ct)
    {
        if (!exchange.IsSelfHosted)
        {
            return;
        }

        var probe = await _ports.ProbeAsync(exchange.PreferredPort, ct).ConfigureAwait(true);
        switch (probe.Status)
        {
            case PortProbeStatus.Ready:
                exchange.EffectivePort = exchange.PreferredPort;
                exchange.PortStatus = PortStatus.Ready;
                break;
            case PortProbeStatus.AutoSelected:
                exchange.EffectivePort = probe.Port!.Value;
                exchange.PortStatus = PortStatus.AutoSelected;
                break;
            default:
                exchange.PortStatus = PortStatus.Unavailable;
                break;
        }
    }

    private async Task PersistPortPreferenceAsync(Guid identityId, int port, CancellationToken ct)
    {
        var entity = await _settings.GetAsync(identityId, ct).ConfigureAwait(false)
            ?? new IdentityLaunchSettings(port, null, false, 0);
        await _settings.SaveAsync(identityId, entity with { PreferredPort = port }, ct).ConfigureAwait(false);
    }

    private async Task PersistBoundPortAsync(Guid identityId, int port, CancellationToken ct)
    {
        var entity = await _settings.GetAsync(identityId, ct).ConfigureAwait(false)
            ?? new IdentityLaunchSettings(port, port, false, 0);
        await _settings.SaveAsync(identityId, entity with { LastBoundPort = port }, ct).ConfigureAwait(false);
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
        foreach (var (identityId, settings) in all)
        {
            var desired = identityId.Value == target.IdentityId && enabled;
            hasTarget |= identityId.Value == target.IdentityId;
            if (settings.AutoStart != desired)
            {
                await _settings
                    .SaveAsync(identityId.Value, settings with { AutoStart = desired }, ct)
                    .ConfigureAwait(false);
            }
        }

        if (!hasTarget)
        {
            await _settings.SaveAsync(
                target.IdentityId,
                new IdentityLaunchSettings(target.PreferredPort, null, enabled, all.Count),
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

    private static void DisposeLeases(
        IEnumerable<PersonaLaunchDescriptor> descriptors,
        IEnumerable<ExchangeLaunchDescriptor>? exchangeDescriptors = null)
    {
        foreach (var descriptor in descriptors)
        {
            descriptor.Lease.Dispose();
        }

        if (exchangeDescriptors is not null)
        {
            foreach (var descriptor in exchangeDescriptors)
            {
                descriptor.Lease.Dispose();
            }
        }
    }

    partial void OnBannerMessageChanged(string? value) => OnPropertyChanged(nameof(HasBanner));
}
