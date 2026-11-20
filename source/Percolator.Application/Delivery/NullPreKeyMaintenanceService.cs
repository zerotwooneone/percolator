using Percolator.Application.Delivery.Ports;

namespace Percolator.Application.Delivery;

/// <summary>
/// No-op <see cref="IPreKeyMaintenanceService"/> registered until DRAIN-10 ships
/// <c>PreKeyMaintenanceService</c>. Keeps the drain path composable; the OPK-consumed
/// trigger is a no-op so prekey top-up simply does not run yet.
/// </summary>
public sealed class NullPreKeyMaintenanceService : IPreKeyMaintenanceService
{
    public void NotifyOneTimePreKeyConsumed(MailboxDrainTarget target) { }

    public Task EvaluateAsync(MailboxDrainTarget target, bool forced, CancellationToken ct = default)
        => Task.CompletedTask;
}
