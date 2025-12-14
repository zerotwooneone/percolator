using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Ingress;

public interface IMessageIngress
{
    Task<IngressResult> DeliverOpaqueAsync(IngressOpaquePayload payload, CancellationToken cancellationToken = default);
}
