using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Application.Network;

namespace Percolator.Application.Ingress;

public sealed class DefaultIngressPipeline : IIngressPipeline
{
    private readonly IIngressReadinessGate _readiness;
    private readonly IIngressValidator _validator;
    private readonly IMediator _mediator;

    public DefaultIngressPipeline(IIngressReadinessGate readiness, IIngressValidator validator, IMediator mediator)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }

    public async Task<IngressResult> DeliverOpaqueAsync(IngressOpaquePayload payload, CancellationToken cancellationToken = default)
    {
        try
        {
            _readiness.EnsureReady();
        }
        catch
        {
            return new IngressResult(IngressDisposition.Rejected_NotReady);
        }

        try
        {
            _validator.Validate(payload);
        }
        catch
        {
            return new IngressResult(IngressDisposition.Rejected_Invalid);
        }

        var command = new DeliverOpaqueMessageCommand
        {
            PayloadBytes = payload.PayloadBytes
        };

        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return new IngressResult(IngressDisposition.Accepted, result.ResponsePayloadBytes);
    }
}
