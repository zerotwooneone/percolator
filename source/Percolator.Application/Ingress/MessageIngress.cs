using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Ingress;

public sealed class MessageIngress : IMessageIngress
{
    private readonly IIngressPipeline _pipeline;

    public MessageIngress(IIngressPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task<IngressResult> DeliverOpaqueAsync(IngressOpaquePayload payload, CancellationToken cancellationToken = default)
    {
        return _pipeline.DeliverOpaqueAsync(payload, cancellationToken);
    }
}
