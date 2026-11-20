// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Result returned by the persona editor dialog. <see cref="IdentityId"/> is populated on
/// create (the generated identity also seeds the deterministic avatar); on edit it is
/// <c>null</c> and the target persona's id is preserved.
/// </summary>
public sealed record PersonaEditorResult(
    PublicIdentityId? IdentityId,
    string DisplayName,
    int PreferredPort);
