using System.Security.Cryptography;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.IntegrationTests.TestDoubles;

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

        // Verify that presentation starts with epoch bytes
        uint presentedEpoch = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(presentation.Span[..4]);
        if (presentedEpoch != epoch) return false;

        // Verify challenge commitment
        Span<byte> expectedChallengeHash = stackalloc byte[32];
        SHA256.HashData(transcriptChallenge, expectedChallengeHash);

        if (!CryptographicOperations.FixedTimeEquals(presentation.Span.Slice(36, 32), expectedChallengeHash))
        {
            return false;
        }

        // Verify non-zero master key material was presented
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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(proof.AsSpan(0, 4), epoch);
        groupMasterSecret32[..Math.Min(32, groupMasterSecret32.Length)].CopyTo(proof.AsSpan(4, 32));
        SHA256.HashData(transcriptChallenge, proof.AsSpan(36, 32));

        return ZkPresentationBytes.FromBytesOwned(proof);
    }
}
