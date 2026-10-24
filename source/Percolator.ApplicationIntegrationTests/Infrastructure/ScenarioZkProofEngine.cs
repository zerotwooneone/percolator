using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2IntegrationTests.Infrastructure;

public sealed class ScenarioZkProofEngine : IZkProofEngine
{
    public bool ShouldFailValidation { get; set; } = false;

    public bool VerifyGroupPresentation(
        uint epoch,
        ZkPresentationBytes presentation,
        ReadOnlySpan<byte> transcriptChallenge,
        ZkGroupPublicParams publicParams)
    {
        if (ShouldFailValidation) return false;
        if (presentation.Span.Length < 68) return false;

        uint presentedEpoch = BinaryPrimitives.ReadUInt32BigEndian(presentation.Span[..4]);
        if (presentedEpoch != epoch) return false;

        Span<byte> expectedChallengeHash = stackalloc byte[32];
        SHA256.HashData(transcriptChallenge, expectedChallengeHash);

        if (!CryptographicOperations.FixedTimeEquals(presentation.Span.Slice(36, 32), expectedChallengeHash))
        {
            return false;
        }

        Span<byte> zero32 = stackalloc byte[32];
        if (CryptographicOperations.FixedTimeEquals(presentation.Span.Slice(4, 32), zero32))
        {
            return false;
        }

        return true;
    }

    public ZkPresentationBytes GenerateGroupPresentation(
        uint epoch,
        ReadOnlySpan<byte> transcriptChallenge,
        ReadOnlySpan<byte> groupMasterSecret32,
        ReadOnlySpan<byte> authCredentialMac)
    {
        var proof = new byte[4 + 32 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(proof.AsSpan(0, 4), epoch);
        groupMasterSecret32[..Math.Min(32, groupMasterSecret32.Length)].CopyTo(proof.AsSpan(4, 32));
        SHA256.HashData(transcriptChallenge, proof.AsSpan(36, 32));

        return ZkPresentationBytes.FromBytesOwned(proof);
    }
}
