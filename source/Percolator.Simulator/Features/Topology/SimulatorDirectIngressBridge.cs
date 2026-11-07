using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Delivery;
using Percolator.Application2.Egress;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Logging;

namespace Percolator.Simulator.Features.Topology;

internal sealed class InboundEnvelopeContextHolder
{
    public AsyncLocal<InboundEnvelope?> CurrentEnvelope { get; } = new();
}

internal sealed class SimulatorDirectEnvelopeUnwrapper : ISealedEnvelopeUnwrapper
{
    private readonly ISealedEnvelopeUnwrapper _inner;
    private readonly InboundEnvelopeContextHolder _contextHolder;
    private readonly ISimulatorLogSink _logSink;
    private readonly string _nodeDisplayName;

    public SimulatorDirectEnvelopeUnwrapper(
        ISealedEnvelopeUnwrapper inner,
        InboundEnvelopeContextHolder contextHolder,
        ISimulatorLogSink logSink,
        string nodeDisplayName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _contextHolder = contextHolder ?? throw new ArgumentNullException(nameof(contextHolder));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _nodeDisplayName = nodeDisplayName;
    }

    public DomainResult<InboundEnvelope> Unwrap(MailboxEnvelope envelope)
    {
        var result = _inner.Unwrap(envelope);
        if (result.IsSuccess)
        {
            _contextHolder.CurrentEnvelope.Value = result.Value;
        }
        else
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                _nodeDisplayName,
                null,
                "TransportIngress",
                $"Direct frame unwrap failure: {result.Error.Code} - {result.Error.Description}"));
        }

        return result;
    }
}

internal sealed class SimulatorIngressDispatchingFilterService : IIngressFilterService
{
    private readonly IIngressFilterService _inner;
    private readonly InboundEnvelopeContextHolder _contextHolder;
    private readonly IInboundIngressPipeline _ingressPipeline;
    private readonly ISimulatorLogSink _logSink;
    private readonly string _nodeDisplayName;
    private readonly OutboxWorker _outboxWorker;

    public SimulatorIngressDispatchingFilterService(
        IIngressFilterService inner,
        InboundEnvelopeContextHolder contextHolder,
        IInboundIngressPipeline ingressPipeline,
        ISimulatorLogSink logSink,
        string nodeDisplayName,
        OutboxWorker outboxWorker)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _contextHolder = contextHolder ?? throw new ArgumentNullException(nameof(contextHolder));
        _ingressPipeline = ingressPipeline ?? throw new ArgumentNullException(nameof(ingressPipeline));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _nodeDisplayName = nodeDisplayName;
        _outboxWorker = outboxWorker ?? throw new ArgumentNullException(nameof(outboxWorker));
    }

    public async ValueTask<DomainResult> CheckIngressAllowedAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        var filterResult = await _inner.CheckIngressAllowedAsync(recipientId, senderId, ct);
        if (!filterResult.IsSuccess)
        {
            return filterResult;
        }

        var envelope = _contextHolder.CurrentEnvelope.Value;
        if (envelope is not null)
        {
            _contextHolder.CurrentEnvelope.Value = null;
            var ingressResult = await _ingressPipeline.ProcessInboundAsync(envelope, ct);
            if (!ingressResult.IsSuccess)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    _nodeDisplayName,
                    null,
                    "TransportIngress",
                    $"Inbound envelope processing failed: {ingressResult.Error.Code} - {ingressResult.Error.Description}"));

                return ingressResult;
            }

            await _outboxWorker.ProcessBatchAsync(50, ct);

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Debug,
                _nodeDisplayName,
                null,
                "TransportIngress",
                $"Inbound {envelope.GetType().Name} from {senderId} processed successfully."));
        }

        return DomainResult.Success();
    }
}
