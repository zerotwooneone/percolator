namespace Percolator.Application.Delivery.Ports;

/// <summary>
/// Hosted pre-key maintenance trigger for drained mailboxes. The drain service fires
/// <see cref="NotifyOneTimePreKeyConsumed"/> after a handshake consumed a one-time
/// pre-key; DRAIN-10 supplies the real implementation (<c>PreKeyMaintenanceService</c>)
/// and replaces the null registration.
/// </summary>
public interface IPreKeyMaintenanceService
{
    /// <summary>Schedules an immediate forced evaluation for <paramref name="target"/>. Fire-and-forget.</summary>
    void NotifyOneTimePreKeyConsumed(MailboxDrainTarget target);

    /// <summary>Runs the throttled status check / signed-prekey rotation / OPK top-up for a drain target.</summary>
    Task EvaluateAsync(MailboxDrainTarget target, bool forced, CancellationToken ct = default);
}
