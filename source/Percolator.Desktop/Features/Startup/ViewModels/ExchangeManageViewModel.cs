// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// Manage surface for an exchange tile: the same persona-link checkbox component as the
/// creation wizard. Checking a persona links it immediately (a vault-local row write);
/// unchecking routes through the "Stop using this Exchange?" confirmation — unlinks are
/// revocations, so they warrant a dialog. Self-hosted exchanges offer the additional
/// local queue-purge option ([INV-DESKTOP-PRESENTATION-MODELS]).
/// </summary>
public partial class ExchangeManageViewModel : ViewModelBase
{
    private readonly IExchangeDirectory _exchanges;
    private readonly IWindowManager _windows;
    private readonly IIdenticonGenerator _identicons;
    private readonly ILogger<ExchangeManageViewModel> _logger;

    private IAuthenticatedSession? _session;
    private Guid _relayIdentityId;
    private bool _suppressToggle;
    private bool _changed;

    public ExchangeManageViewModel(
        IExchangeDirectory exchanges,
        IWindowManager windows,
        IIdenticonGenerator identicons,
        ILogger<ExchangeManageViewModel> logger)
    {
        _exchanges = exchanges ?? throw new ArgumentNullException(nameof(exchanges));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _identicons = identicons ?? throw new ArgumentNullException(nameof(identicons));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised when the dialog should close; the window owns closing.</summary>
    public event EventHandler? RequestClose;

    /// <summary><c>true</c> when any link state changed — the gate refreshes the tile strip.</summary>
    public bool Result { get; private set; }

    /// <summary>Exchange display name shown in the dialog header.</summary>
    public string ExchangeDisplayName { get; private set; } = string.Empty;

    /// <summary>Self-hosted exchanges get the badge, profile accent, and queue-purge option.</summary>
    public bool IsSelfHosted { get; private set; }

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Persona checkbox rows, pre-checked for personas already linked.</summary>
    public IReadOnlyList<LinkablePersonaModel> Personas { get; private set; } =
        Array.Empty<LinkablePersonaModel>();

    /// <summary>Seeds the surface for the given exchange tile and current persona roster.</summary>
    public void Initialize(
        IAuthenticatedSession session,
        ExchangeTileViewModel exchange,
        IReadOnlyList<PersonaDirectoryEntry> personas)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(personas);

        _session = session;
        _relayIdentityId = exchange.RelayIdentityId;
        _changed = false;
        Result = false;
        ErrorMessage = null;
        ExchangeDisplayName = exchange.DisplayName;
        IsSelfHosted = exchange.IsSelfHosted;
        OnPropertyChanged(nameof(ExchangeDisplayName));
        OnPropertyChanged(nameof(IsSelfHosted));

        var linked = new HashSet<Guid>(exchange.LinkedPersonaIds);
        Personas = personas
            .Select(p => new LinkablePersonaModel(
                p.IdentityId,
                p.DisplayName,
                AvatarPresentation.FromIdenticon(_identicons.Generate(p.IdentityId)),
                isLinked: linked.Contains(p.IdentityId),
                notifyChanged: model => TogglePersonaCommand.Execute(model)))
            .ToList();
        OnPropertyChanged(nameof(Personas));
    }

    /// <summary>
    /// Checkbox toggle: check links immediately; uncheck opens the revocation dialog.
    /// A cancelled or failed unlink reverts the checkbox.
    /// </summary>
    [RelayCommand]
    private async Task TogglePersonaAsync(LinkablePersonaModel? persona, System.Threading.CancellationToken ct)
    {
        if (persona is null || _session is null || _suppressToggle)
        {
            return;
        }

        if (persona.IsLinked)
        {
            var result = await _exchanges
                .LinkPersonaAsync(_session, new PublicIdentityId(_relayIdentityId), new PublicIdentityId(persona.PersonaId), ct)
                .ConfigureAwait(true);
            if (result.IsFailure)
            {
                _logger.LogWarning("Exchange link failed for {PersonaId}: {Error}", persona.PersonaId, result.Error.Description);
                Revert(persona, isLinked: false);
                ErrorMessage = result.Error.Description;
                return;
            }
        }
        else
        {
            StopUsingExchangeDialogViewModel? dialog = null;
            var confirmed = await _windows
                .ShowDialogAsync<StopUsingExchangeDialogViewModel, bool>(
                    vm =>
                    {
                        vm.Initialize(persona.DisplayName, ExchangeDisplayName, IsSelfHosted);
                        dialog = vm;
                    })
                .ConfigureAwait(true);
            if (confirmed != true)
            {
                Revert(persona, isLinked: true);
                return;
            }

            var result = await _exchanges
                .UnlinkPersonaAsync(
                    _session,
                    new PublicIdentityId(_relayIdentityId),
                    new PublicIdentityId(persona.PersonaId),
                    purgeQueuedEnvelopes: IsSelfHosted && dialog?.PurgeQueuedEnvelopes == true,
                    ct)
                .ConfigureAwait(true);
            if (result.IsFailure)
            {
                _logger.LogWarning("Exchange unlink failed for {PersonaId}: {Error}", persona.PersonaId, result.Error.Description);
                Revert(persona, isLinked: true);
                ErrorMessage = result.Error.Description;
                return;
            }
        }

        ErrorMessage = null;
        _changed = true;
    }

    /// <summary>[ Done ] — closes with the changed-flag result.</summary>
    [RelayCommand]
    private void Done()
    {
        Result = _changed;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void Revert(LinkablePersonaModel persona, bool isLinked)
    {
        _suppressToggle = true;
        try
        {
            persona.IsLinked = isLinked;
        }
        finally
        {
            _suppressToggle = false;
        }
    }
}
