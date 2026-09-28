using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Tests.TestDoubles;

public sealed class FakeZkProofEngine : IZkProofEngine
{
    public bool AlwaysValid { get; set; } = true;
    public uint? ExpectedEpoch { get; set; }
    public byte[]? ExpectedTranscriptChallenge { get; set; }

    public bool VerifyGroupPresentation(
        uint epoch,
        ZkPresentationBytes presentation,
        ReadOnlySpan<byte> transcriptChallenge,
        ZkGroupPublicParams publicParams)
    {
        if (!AlwaysValid) return false;
        if (ExpectedEpoch.HasValue && ExpectedEpoch.Value != epoch) return false;
        if (ExpectedTranscriptChallenge != null && !transcriptChallenge.SequenceEqual(ExpectedTranscriptChallenge)) return false;
        return presentation.Length > 0 && publicParams.Length > 0;
    }

    public ZkPresentationBytes GenerateGroupPresentation(
        uint epoch,
        ReadOnlySpan<byte> transcriptChallenge,
        ReadOnlySpan<byte> groupMasterSecret32,
        ReadOnlySpan<byte> authCredentialMac)
    {
        var dummy = new byte[64];
        dummy[0] = (byte)(epoch & 0xFF);
        return ZkPresentationBytes.FromBytesOwned(dummy);
    }
}
