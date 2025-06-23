using System.Threading.Tasks;
using Percolator.Contracts.Protos;

namespace Percolator.Application.Manifests;

public interface ISignatureService
{
    Task SignAsync(SignedManifest manifest);

    Task<bool> VerifyAsync(SignedManifest manifest);
}
