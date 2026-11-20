// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Domain.Identities;
using R3;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Session-scoped presentation state service holding and publishing the active persona
/// in the desktop shell (INV-DESKTOP-STATEFUL-PROJECTIONS, DEP-DESKTOP-PERSONA-ISOLATION).
/// Operates on in-memory presentation models initialized at session handoff.
/// </summary>
public sealed class CurrentPersonaService : ICurrentPersonaService, IDisposable
{
    private readonly ILogger<CurrentPersonaService> _logger;
    private readonly Subject<ActivePersonaModel?> _currentPersonaChanged = new();
    private readonly List<ActivePersonaModel> _activePersonas = new();
    private readonly object _gate = new();
    private ActivePersonaModel? _currentPersona;
    private bool _disposed;

    public CurrentPersonaService(ILogger<CurrentPersonaService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ActivePersonaModel? CurrentPersona
    {
        get
        {
            lock (_gate) return _currentPersona;
        }
    }

    public IReadOnlyList<ActivePersonaModel> ActivePersonas
    {
        get
        {
            lock (_gate) return _activePersonas.ToList();
        }
    }

    public Observable<ActivePersonaModel?> CurrentPersonaChanged => _currentPersonaChanged;

    public void InitializeActivePersonas(IEnumerable<ActivePersonaModel> personas)
    {
        ArgumentNullException.ThrowIfNull(personas);

        lock (_gate)
        {
            _activePersonas.Clear();
            _activePersonas.AddRange(personas);

            if (_currentPersona == null && _activePersonas.Count > 0)
            {
                _currentPersona = _activePersonas[0];
                _logger.LogInformation("Current persona initialized to primary: {IdentityId} ({DisplayName})",
                    _currentPersona.IdentityId, _currentPersona.DisplayName);
                _currentPersonaChanged.OnNext(_currentPersona);
            }
        }
    }

    public void SelectPersona(ActivePersonaModel persona)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(persona);

        lock (_gate)
        {
            if (!_activePersonas.Any(p => p.IdentityId == persona.IdentityId))
            {
                _activePersonas.Add(persona);
            }

            _currentPersona = persona;
            _logger.LogInformation("Current persona selected: {IdentityId} ({DisplayName}) on port {Port}",
                persona.IdentityId, persona.DisplayName, persona.BoundPort);
        }

        _currentPersonaChanged.OnNext(persona);
    }

    public bool SelectPersonaById(PublicIdentityId personaId)
    {
        if (_disposed || !personaId.IsValid) return false;

        ActivePersonaModel? selected = null;
        lock (_gate)
        {
            selected = _activePersonas.FirstOrDefault(p => p.IdentityId == personaId);
            if (selected == null)
            {
                _logger.LogWarning("Cannot select persona {PersonaId}; not found in active session personas", personaId);
                return false;
            }

            _currentPersona = selected;
            _logger.LogInformation("Current persona switched to {IdentityId} ({DisplayName})",
                selected.IdentityId, selected.DisplayName);
        }

        _currentPersonaChanged.OnNext(selected);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _currentPersonaChanged.Dispose();
    }
}
