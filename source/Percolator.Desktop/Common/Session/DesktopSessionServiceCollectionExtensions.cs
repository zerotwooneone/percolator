namespace Percolator.Desktop.Common.Session;

using System;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Session-container registrations owned by the Desktop executable. CTX-02 provides the
/// composition seam only; CTX-03 fills this in with the Stage-1 forwards the session VM
/// and service graph needs.
/// </summary>
internal static class DesktopSessionServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopSessionServices(
        this IServiceCollection services,
        IServiceProvider stage1)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stage1);

        // CTX-03: register post-unlock Desktop services + Stage-1 forwards here.
        return services;
    }
}
