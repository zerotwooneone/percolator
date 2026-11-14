// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Ports;
using Percolator.Desktop.Features.Startup.Models;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Orchestrates the two-stage DI window transition ([INV-DESKTOP-TWO-STAGE-HANDOFF]):
/// startup gate navigation, startup-to-shell swap, and secure relock.
/// </summary>
public interface IWindowHandoffCoordinator
{
    /// <summary>
    /// Navigates the open <c>StartupWindow</c> forward to Screen 2 (Identity Gate).
    /// </summary>
    /// <param name="session">
    /// The authenticated Stage 2 session produced by vault unlock/initialize — always
    /// present, including the duress decoy (a real session or the unlock fails).
    /// Parked on the startup host until personas launch the shell.
    /// </param>
    Task TransitionToGateAsync(IAuthenticatedSession session, CancellationToken ct = default);

    /// <summary>
    /// Starts the authenticated session's hosted services, swaps <c>StartupWindow</c> for
    /// <c>MainWindow</c>, and closes the startup frame.
    /// </summary>
    /// <param name="personas">
    /// Launch descriptors acquired at the Identity Gate; the coordinator takes ownership of
    /// their port leases and releases them on <see cref="LockSessionAsync"/>.
    /// </param>
    /// <param name="exchanges">
    /// Self-hosted exchange descriptors acquired at the Identity Gate; their port leases
    /// are held and released with the persona leases on <see cref="LockSessionAsync"/>.
    /// </param>
    Task TransitionToMainShellAsync(
        IAuthenticatedSession session,
        IReadOnlyList<PersonaLaunchDescriptor>? personas = null,
        IReadOnlyList<ExchangeLaunchDescriptor>? exchanges = null,
        CancellationToken ct = default);

    /// <summary>
    /// Securely relocks: stops the authenticated host, zeroizes keys, closes <c>MainWindow</c>,
    /// and re-opens <c>StartupWindow</c> in the unlock state.
    /// </summary>
    Task LockSessionAsync(CancellationToken ct = default);
}
