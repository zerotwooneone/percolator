using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security.Ports;

public interface IZkProofEngine
{
    bool VerifyGroupPresentation(
        uint epoch,
        ZkPresentationBytes presentation,
        ReadOnlySpan<byte> transcriptChallenge,
        ZkGroupPublicParams publicParams);
}
