using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts.Protos;

namespace Percolator.Application.Manifests;

public interface ISignatureService
{
    Task<SignedManifest> SignManifestAsync(Manifest manifest, CancellationToken cancellationToken=default);

    Task<bool> VerifyManifestAsync(SignedManifest signedManifest, CancellationToken cancellationToken=default);
}
