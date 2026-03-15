namespace Percolator.Application.Ingress;

public interface IMessageIngress
{
    Task<IngressResult> DeliverOpaqueAsync(IngressOpaquePayload payload, CancellationToken cancellationToken = default);
}
