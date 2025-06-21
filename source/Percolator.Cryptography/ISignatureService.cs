using Percolator.Contracts.Protos;

namespace Percolator.Cryptography;

public interface ISignatureService
{
    void Sign(SignedManifest manifest);
    bool Verify(SignedManifest manifest);
}
