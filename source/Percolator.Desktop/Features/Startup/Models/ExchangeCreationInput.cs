// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Collections.Generic;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Input parameters for configuring the Exchange Creation wizard dialog ([INV-DESKTOP-PRESENTATION-MODELS]).
/// </summary>
public sealed record ExchangeCreationInput(
    IReadOnlyList<PersonaDirectoryEntry> Personas,
    int? InitialPreferredPort = null);
