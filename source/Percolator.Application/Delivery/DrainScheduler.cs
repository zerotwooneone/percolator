using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Events;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Application.Session;
using Percolator.PluginSdk.Events;
using Percolator.Application.Session.Events;
using Percolator.Application.Session.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application.Delivery;

/// <summary>
/// Reconciles the active-persona mailbox bindings from <see cref="IRelayDirectory"/>
/// against running drain streams, then supervises one
/// <see cref="MailboxDrainService"/> stream per <see cref="MailboxDrainTarget"/>:
/// mailbox ensure-provisioned → fresh token resolution → first-connect prekey seed
/// → spool replay → streaming drain with persist-then-ack.
/// Session-cap admission, per-target exponential backoff, and a periodic
/// <see cref="DrainSchedulerOptions.MaxSessionAge"/> rotation bound reconnect cost
/// and token staleness. Event-bus reconcile triggers keep the loop event-driven
/// rather than polled per-target.
/// </summary>
public sealed class DrainScheduler
{
    private readonly IActiveIdentitySet _activeIdentities;
    private readonly IEventSubscriber _events;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _timeProvider;
    private readonly DrainSchedulerOptions _options;
    private readonly ILogger<DrainScheduler> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<MailboxDrainTarget, (CancellationTokenSource Cts, Task Task)> _supervisors = new();
    private readonly List<(MailboxDrainTarget Target, DateTimeOffset DueAtUtc)> _dueQueue = new();
    private readonly Dictionary<MailboxDrainTarget, int> _failureCounts = new();
    private readonly HashSet<MailboxDrainTarget> _firstActivationSeen = new();
    private readonly HashSet<MailboxDrainTarget> _establishedSessions = new();

    // Bounded wake-up channel: any number of pending signals collapses to one
    // reconcile iteration (DropWrite when a signal is already queued).
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private void Wake() => _signal.Writer.TryWrite(true);

    /// <summary>
    /// True while the target's supervisor holds a connected stream — i.e., the initial
    /// backlog has been pushed and live envelopes are flowing. False while the target
    /// is queued, reconnecting, or in backoff.
    /// </summary>
    public bool HasEstablishedSession(MailboxDrainTarget target)
    {
        lock (_gate)
        {
            return _establishedSessions.Contains(target);
        }
    }

    public DrainScheduler(
        IActiveIdentitySet activeIdentities,
        IEventSubscriber events,
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider timeProvider,
        DrainSchedulerOptions options,
        ILogger<DrainScheduler> logger)
    {
        _activeIdentities = activeIdentities ?? throw new ArgumentNullException(nameof(activeIdentities));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var activated = _events.Subscribe<PersonaActivatedEvent>((_, _) =>
        {
            Wake();
            return ValueTask.CompletedTask;
        });
        using var deactivated = _events.Subscribe<PersonaDeactivatedEvent>((_, _) =>
        {
            Wake();
            return ValueTask.CompletedTask;
        });
        using var bindingChanged = _events.Subscribe<MailboxBindingChangedEvent>((_, _) =>
        {
            Wake();
            return ValueTask.CompletedTask;
        });
        using var reconcileTimer = new PeriodicTimer(_options.ReconcileInterval);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reconcileTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    Wake();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            AdmitReadyTargets(stoppingToken);

            var delay = NextDelay();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(delay);
            try
            {
                await _signal.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
                while (_signal.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // due-time elapsed
            }
        }

        MailboxDrainTarget[] toCancel;
        lock (_gate)
        {
            toCancel = _supervisors.Keys.ToArray();
        }
        foreach (var target in toCancel)
        {
            await CancelSupervisorAsync(target).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyList<RelayDirectoryEntry> entries;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var directory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
            entries = await directory.ListAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay directory reconcile failed; keeping current drain targets");
            return;
        }

        var active = _activeIdentities.Current;
        var desired = entries
            .SelectMany(e => e.EnabledOwnerIdentityIds
                .Where(active.Contains)
                .Select(o => new MailboxDrainTarget(o, e.RelayIdentityId, e.Endpoint)))
            .ToHashSet();


        List<MailboxDrainTarget> toCancel;
        List<MailboxDrainTarget> toEnqueue;
        lock (_gate)
        {
            toCancel = _supervisors.Keys
                .Concat(_dueQueue.Select(d => d.Target))
                .Where(t => !desired.Contains(t))
                .Distinct()
                .ToList();
            toEnqueue = desired
                .Where(t => !_supervisors.ContainsKey(t) &&
                            !_dueQueue.Any(d => d.Target == t))
                .ToList();
        }

        foreach (var target in toCancel)
        {
            // Also drops any queued-but-not-started entry.
            lock (_gate)
            {
                _dueQueue.RemoveAll(d => d.Target == target);
            }
            await CancelSupervisorAsync(target).ConfigureAwait(false);
        }

        var now = _timeProvider.UtcNow;
        lock (_gate)
        {
            foreach (var target in toEnqueue)
            {
                _dueQueue.Add((target, now));
            }
        }
    }

    private void AdmitReadyTargets(CancellationToken ct)
    {
        while (true)
        {
            MailboxDrainTarget target;
            lock (_gate)
            {
                if (_supervisors.Count >= _options.MaxConcurrentSessions || _dueQueue.Count == 0)
                {
                    return;
                }

                var now = _timeProvider.UtcNow;
                var index = _dueQueue.FindIndex(d => d.DueAtUtc <= now);
                if (index < 0)
                {
                    return;
                }

                target = _dueQueue[index].Target;
                _dueQueue.RemoveAt(index);
                if (_supervisors.ContainsKey(target))
                {
                    // Stale duplicate queued while this target was mid-start — drop it.
                    continue;
                }
            }

            var targetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = SuperviseTargetAsync(target, targetCts.Token);
            lock (_gate)
            {
                // A synchronously-faulting supervisor completes before we get here;
                // its finally already ran, so registering the finished task would
                // leave a ghost entry that blocks re-enqueue and fills the cap.
                if (!task.IsCompleted)
                {
                    _supervisors[target] = (targetCts, task);
                }
                else
                {
                    targetCts.Dispose();
                }
            }
        }
    }

    private TimeSpan NextDelay()
    {
        var now = _timeProvider.UtcNow;
        var nextTick = now + _options.ReconcileInterval;
        lock (_gate)
        {
            if (_dueQueue.Count > 0)
            {
                var earliest = _dueQueue.Min(d => d.DueAtUtc);
                if (earliest < nextTick)
                {
                    nextTick = earliest;
                }
            }
        }

        var delay = nextTick - now;
        // Small floor: never busy-loop when a due entry waits on a full session cap.
        return delay > TimeSpan.FromMilliseconds(5) ? delay : TimeSpan.FromMilliseconds(5);
    }

    private async Task SuperviseTargetAsync(MailboxDrainTarget target, CancellationToken ct)
    {
        var normalEnd = false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var drainService = scope.ServiceProvider.GetRequiredService<MailboxDrainService>();
            var relayDirectory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
            var mailboxClient = scope.ServiceProvider.GetRequiredService<IRelayMailboxClient>();
            var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
            var preKeyMaintenance = scope.ServiceProvider.GetRequiredService<IPreKeyMaintenanceService>();

            // DRAIN-07 provision-on-connect: the relay-side mailbox must exist before
            // the stream opens. Failure lands in the shared fault path (backoff +
            // requeue). DRAIN-10 adds IPreKeyMaintenanceService.EvaluateAsync here.
            var ensured = await relayDirectory.EnsureMailboxProvisionedAsync(
                target.RelayIdentityId, target.OwnerIdentityId, ct).ConfigureAwait(false);
            if (ensured.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Mailbox provisioning failed for {target.RelayIdentityId.Value}: {ensured.Error.Code} {ensured.Error.Description}");
            }

            // DRAIN-10: throttled hosted-prekey evaluation (status check, SPK
            // rotation, OPK top-up). A maintenance fault must not take down drain
            // supervision — the envelope stream is the primary path.
            try
            {
                await preKeyMaintenance.EvaluateAsync(target, forced: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Pre-key maintenance evaluation failed for {Relay}; drain continues.",
                    target.RelayIdentityId.Value);
            }

            // Tokens are resolved fresh per connect — never cached across reconnects.
            var tokens = await relayDirectory.GetMailboxTokensAsync(
                target.OwnerIdentityId, target.RelayIdentityId, ct).ConfigureAwait(false);
            if (tokens.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Mailbox token resolution failed for {target.RelayIdentityId.Value}: {tokens.Error.Code} {tokens.Error.Description}");
            }

            // DRAIN-07: first connect per session also seeds the hosted bundle —
            // personas launched through the auto-start path never ran the gate
            // launch's provision+publish. Until DRAIN-10's status RPC lands,
            // GetPreKeyStatusAsync reports RELAY_PREKEY_STATUS_UNAVAILABLE — treat
            // a failed check as unknown bundle state and publish anyway; relay-side
            // publication merges OPKs idempotently.
            var firstConnect = false;
            lock (_gate)
            {
                firstConnect = _firstActivationSeen.Add(target);
            }
            if (firstConnect)
            {
                try
                {
                    var status = await provisioner.GetPreKeyStatusAsync(
                        DeviceId.Primary, target.Endpoint, tokens.Value!, ct).ConfigureAwait(false);
                    if (status.IsFailure || !status.Value!.Found)
                    {
                        var published = await provisioner.PublishPreKeyBundleAsync(
                            target.OwnerIdentityId, DeviceId.Primary, target.Endpoint, tokens.Value!, 100u, ct: ct).ConfigureAwait(false);
                        if (published.IsFailure)
                        {
                            throw new InvalidOperationException(
                                $"PreKey bundle publication failed for {target.RelayIdentityId.Value}: {published.Error.Code} {published.Error.Description}");
                        }
                    }
                }
                catch
                {
                    // Unmark on any failure — a faulted seed must not permanently
                    // skip the publish path; the backoff retry re-runs the block.
                    lock (_gate)
                    {
                        _firstActivationSeen.Remove(target);
                    }
                    throw;
                }
            }

            var replay = await drainService.ReplayPendingAsync(target, ct).ConfigureAwait(false);
            if (replay.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Pending-ingress replay failed for {target.RelayIdentityId.Value}: {replay.Error.Code} {replay.Error.Description}");
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sessionCts.CancelAfter(_options.MaxSessionAge);
            var opened = await mailboxClient.OpenMailboxStreamAsync(
                target.Endpoint, tokens.Value, sessionCts.Token).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Mailbox stream open failed for {target.RelayIdentityId.Value}: {opened.Error.Code} {opened.Error.Description}");
            }

            await using var session = opened.Value;
            lock (_gate)
            {
                _establishedSessions.Add(target);
            }
            await foreach (var queued in session.Envelopes.WithCancellation(sessionCts.Token).ConfigureAwait(false))
            {
                // Per-envelope scope so scoped stores/pipeline see a fresh ambient context.
                using var envelopeScope = _scopeFactory.CreateScope();
                var envelopeDrain = envelopeScope.ServiceProvider.GetRequiredService<MailboxDrainService>();
                // targetCts (ct), not sessionCts.Token: an in-flight envelope finishes
                // cleanly when MaxSessionAge elapses.
                _ = await envelopeDrain.ReceiveAsync(target, queued, session, ct).ConfigureAwait(false);
            }

            normalEnd = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Target removal or shutdown — not a failure, do not re-enqueue.
        }
        catch (OperationCanceledException)
        {
            // MaxSessionAge elapsed mid-stream — normal rotation.
            normalEnd = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Mailbox drain for owner {OwnerId} on relay {RelayId} faulted",
                target.OwnerIdentityId.Value, target.RelayIdentityId.Value);
        }
        finally
        {
            lock (_gate)
            {
                _supervisors.Remove(target);
                _establishedSessions.Remove(target);
            }
        }

        if (!ct.IsCancellationRequested)
        {
            var now = _timeProvider.UtcNow;
            lock (_gate)
            {
                if (normalEnd)
                {
                    _failureCounts.Remove(target);
                    _dueQueue.Add((target, now));
                }
                else
                {
                    var failures = _failureCounts.TryGetValue(target, out var f) ? f + 1 : 1;
                    _failureCounts[target] = failures;
                    var backoff = TimeSpan.FromTicks(Math.Min(
                        _options.MaxBackoff.Ticks,
                        (long)(_options.InitialBackoff.Ticks * Math.Pow(_options.BackoffMultiplier, failures - 1))));
                    _dueQueue.Add((target, now + backoff));
                }
            }
        }

        // Wake after the requeue: the due entry must be visible before the admission
        // loop recomputes its wait, and a freed session-cap slot admits queued targets.
        Wake();
    }

    private async Task CancelSupervisorAsync(MailboxDrainTarget target)
    {
        (CancellationTokenSource Cts, Task Task) supervisor;
        lock (_gate)
        {
            if (!_supervisors.Remove(target, out supervisor))
            {
                return;
            }
        }

        supervisor.Cts.Cancel();
        try
        {
            await supervisor.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drain supervisor for {RelayId} faulted during teardown", target.RelayIdentityId.Value);
        }
        finally
        {
            supervisor.Cts.Dispose();
        }
    }
}
