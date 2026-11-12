// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;

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
    /// The authenticated Stage 2 session produced by vault unlock/initialize, if any.
    /// Parked on the startup host until personas launch the shell (duress passes <c>null</c>).
    /// </param>
    Task TransitionToGateAsync(IAuthenticatedSession? session = null, CancellationToken ct = default);

    /// <summary>
    /// Starts the authenticated session's hosted services, swaps <c>StartupWindow</c> for
    /// <c>MainWindow</c>, and closes the startup frame.
    /// </summary>
    Task TransitionToMainShellAsync(IAuthenticatedSession session, CancellationToken ct = default);

    /// <summary>
    /// Securely relocks: stops the authenticated host, zeroizes keys, closes <c>MainWindow</c>,
    /// and re-opens <c>StartupWindow</c> in the unlock state.
    /// </summary>
    Task LockSessionAsync(CancellationToken ct = default);
}
