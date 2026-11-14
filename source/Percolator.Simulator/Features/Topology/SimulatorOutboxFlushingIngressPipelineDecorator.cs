using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery;
using Percolator.Application.Ingress;
using Percolator.Domain.Common;
using Percolator.Simulator.Features.Logging;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatorOutboxFlushingIngressPipelineDecorator : IInboundIngressPipeline
{
    private readonly IInboundIngressPipeline _inner;
    private readonly OutboxWorker _outboxWorker;
    private readonly ISimulatorLogSink _logSink;
    private readonly string _nodeDisplayName;

    public SimulatorOutboxFlushingIngressPipelineDecorator(
        IInboundIngressPipeline inner,
        OutboxWorker outboxWorker,
        ISimulatorLogSink logSink,
        string nodeDisplayName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outboxWorker = outboxWorker ?? throw new ArgumentNullException(nameof(outboxWorker));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _nodeDisplayName = nodeDisplayName ?? throw new ArgumentNullException(nameof(nodeDisplayName));
    }

    public async ValueTask<DomainResult> ProcessInboundAsync(InboundEnvelope envelope, CancellationToken ct = default)
    {
        var result = await _inner.ProcessInboundAsync(envelope, ct);
        if (result.IsSuccess)
        {
            await _outboxWorker.ProcessBatchAsync(50, ct);

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Debug,
                _nodeDisplayName,
                null,
                "TransportIngress",
                $"Inbound {envelope.GetType().Name} from {envelope.SenderIdentityId} processed successfully."));
        }
        else
        {
            var logLevel = result.Error.Code == "EXPIRED_OR_DUPLICATE_MESSAGE"
                ? LogLevel.Debug
                : LogLevel.Warning;

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                logLevel,
                _nodeDisplayName,
                null,
                "TransportIngress",
                $"Inbound envelope processing failed: {result.Error.Code} - {result.Error.Description}"));
        }

        return result;
    }
}
