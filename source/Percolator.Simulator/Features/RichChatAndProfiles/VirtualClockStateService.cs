using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Delivery;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;
namespace Percolator.Simulator.Features.RichChatAndProfiles;
internal sealed class VirtualClockStateService : IVirtualClockStateService, IDisposable
{
    private readonly ReactiveProperty<DateTimeOffset> _currentTime;
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatorLogSink _logSink;
    public ReadOnlyReactiveProperty<DateTimeOffset> CurrentUtcNow => _currentTime;
    public int CurrentDiscoveryEpoch => (int)(_currentTime.Value.ToUnixTimeSeconds() / 86400);
    public DateTimeOffset UtcNow => _currentTime.Value;
    public VirtualClockStateService(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _currentTime = new ReactiveProperty<DateTimeOffset>(DateTimeOffset.UtcNow);
    }
    public void AdvanceTime(TimeSpan delta)
    {
        UiTaskRunner.FireAndForget(
            () => AdvanceTimeAsync(delta),
            category: "VirtualClock",
            logSink: _logSink);
    }
    public async Task AdvanceTimeAsync(TimeSpan delta, CancellationToken ct = default)
    {
        await _advanceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var newTime = _currentTime.Value.Add(delta);
            _currentTime.Value = newTime;
            _logSink.Log(new SimulatorLogEntry(
                newTime,
                LogLevel.Information,
                "VirtualClock",
                null,
                "TimeAcceleration",
                $"Virtual clock advanced by {delta}. Current simulated UTC: {newTime:yyyy-MM-dd HH:mm:ss}"));
            var hosts = _worldState.GetAllPersonaHosts();
            foreach (var host in hosts)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var scope = host.Services.CreateScope();
                    var chatRepo = scope.ServiceProvider.GetRequiredService<IChatMessageRepository>();
                    var nowMs = (ulong)newTime.ToUnixTimeMilliseconds();
                    var purgedCount = await chatRepo.PurgeExpiredMessagesAsync(nowMs, ct).ConfigureAwait(false);
                    if (purgedCount > 0)
                    {
                        _stateBus.Publish(new SimulatorStateSignal(
                            host.Id,
                            SignalCategory.ChatTranscriptChanged,
                            Details: host.PublicIdentityId.ToString()));
                    }
                    await host.FlushOutboxAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logSink.Log(new SimulatorLogEntry(
                        newTime,
                        LogLevel.Warning,
                        host.DisplayName,
                        host.Id,
                        "VirtualClock",
                        $"Error processing clock advance on host {host.DisplayName}: {ex.Message}"));
                }
            }
        }
        finally
        {
            _advanceGate.Release();
        }
    }
    public void Dispose()
    {
        _currentTime.Dispose();
        _advanceGate.Dispose();
    }
}
