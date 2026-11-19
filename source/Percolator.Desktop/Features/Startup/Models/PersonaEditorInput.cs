using Percolator.Domain.Identities;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Input parameters for configuring the Create/Edit Persona dialog.
/// </summary>
public sealed record PersonaEditorInput(
    bool IsEdit,
    string? DisplayName = null,
    int? PreferredPort = null,
    PublicIdentityId? IdentityId = null)
{
    public static PersonaEditorInput ForCreate(int? preferredPort = null) =>
        new(false, PreferredPort: preferredPort);

    public static PersonaEditorInput ForEdit(string displayName, int preferredPort, PublicIdentityId identityId) =>
        new(true, displayName, preferredPort, identityId);
}
