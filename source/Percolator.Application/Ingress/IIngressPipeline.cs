namespace Percolator.Application.Ingress;

public interface IIngressPipeline
{
    Task<IngressResult> DeliverOpaqueAsync(IngressOpaquePayload payload, CancellationToken cancellationToken = default);
}
