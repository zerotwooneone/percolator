using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Chaos;

internal sealed class SimulatorChaosTransportDispatcherDecorator : ITransportDispatcher
{
    private readonly ITransportDispatcher _inner;
    private readonly ISimulatorChaosController _chaos;
    private readonly SimulatedNodeId _nodeId;
    private readonly ISimulatorLogSink _logSink;

    public SimulatorChaosTransportDispatcherDecorator(
        ITransportDispatcher inner,
        ISimulatorChaosController chaos,
        SimulatedNodeId nodeId,
        ISimulatorLogSink logSink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _chaos = chaos ?? throw new ArgumentNullException(nameof(chaos));
        _nodeId = nodeId;
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var fault = _chaos.EvaluateDeliveryFault(_nodeId);
        if (fault == ChaosFaultAction.DropEnvelope)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                _nodeId.ToString(),
                _nodeId,
                "Chaos",
                $"Outbound envelope {job.Id} dropped by simulator chaos controller."));

            return DomainResult.Failure(new DomainError(
                "CHAOS_DROPPED",
                "Envelope dropped by simulator chaos controller fault injection."));
        }

        if (fault == ChaosFaultAction.SimulateTransientError)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                _nodeId.ToString(),
                _nodeId,
                "Chaos",
                $"Simulating transient transport error for envelope {job.Id}."));

            return DomainResult.Failure(new DomainError(
                "TRANSIENT_FAULT",
                "Transient transport error simulated by chaos controller."));
        }

        if (fault == ChaosFaultAction.InjectArtificialDelay)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                _nodeId.ToString(),
                _nodeId,
                "Chaos",
                $"Injecting 500ms delay on envelope {job.Id}."));

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return await _inner.SendAsync(job, ct).ConfigureAwait(false);
    }
}
