using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Presentation-facing persona facade ([VM-004], [VM-006]): ViewModels stay free of
/// session internals and write-side aggregates. Resolves from the authenticated session
/// container — including the decoy session produced by duress activation, which is a real
/// vault session so personas created during duress persist and subsequent duress logins
/// look authentic.
/// </summary>
public interface IPersonaDirectory
{
    /// <summary>Lists personas merged with their desktop launch settings, ordered for display.</summary>
    Task<IReadOnlyList<PersonaDirectoryEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Generates a fresh identity keypair and persists the persona profile, keys, and
    /// initial launch settings.
    /// </summary>
    Task<PersonaDirectoryEntry> CreateAsync(
        PublicIdentityId identityId,
        string displayName,
        int preferredPort,
        CancellationToken ct = default);

    /// <summary>Renames an existing persona (edit-profile path).</summary>
    Task RenameAsync(
        PublicIdentityId identityId,
        string displayName,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently removes the persona: identity keys, profile row, and launch settings.
    /// </summary>
    Task ShredAndDeleteAsync(PublicIdentityId identityId, CancellationToken ct = default);
}
