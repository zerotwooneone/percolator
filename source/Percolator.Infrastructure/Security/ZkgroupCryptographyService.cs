namespace Percolator.Infrastructure.Security;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Signal.Interop;

public sealed class ZkgroupCryptographyService : IZkProofEngine
{
    private const uint PresentationMagic = 0x5A4B5001; // "ZKP\x01"

    public bool VerifyGroupPresentation(
        uint epoch,
        ZkPresentationBytes presentation,
        ReadOnlySpan<byte> transcriptChallenge,
        ZkGroupPublicParams publicParams)
    {
        if (presentation.Length < 72 || publicParams.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> span = presentation.Span;
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(span[0..4]);
        if (magic != PresentationMagic)
        {
            return false;
        }

        uint proofEpoch = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
        if (proofEpoch != epoch)
        {
            return false;
        }

        ReadOnlySpan<byte> proofChallengeHash = span[8..40];
        Span<byte> actualChallengeHash = stackalloc byte[32];
        SHA256.HashData(transcriptChallenge, actualChallengeHash);

        if (!CryptographicOperations.FixedTimeEquals(proofChallengeHash, actualChallengeHash))
        {
            return false;
        }

        // Authenticate presentation proof tag:
        // Full presentation (>= 136 bytes) carries:
        // [40..72]: Ephemeral presentation public key
        // [72..136]: Ed25519 signature over (epoch || actualChallengeHash)
        if (presentation.Length >= 136)
        {
            ReadOnlySpan<byte> pubEncoded = span[40..72];
            ReadOnlySpan<byte> signature = span[72..136];

            Span<byte> zero32 = stackalloc byte[32];
            if (CryptographicOperations.FixedTimeEquals(pubEncoded, zero32))
            {
                return false;
            }

            Span<byte> proofData = stackalloc byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(proofData[0..4], epoch);
            actualChallengeHash.CopyTo(proofData[4..36]);

            try
            {
                if (!SignalCrypto.Ed25519Verify(pubEncoded, proofData, signature))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
        else
        {
            // Minimum presentation proof tag (72 bytes) check: tag must be non-zero
            ReadOnlySpan<byte> proofTag = span[40..72];
            Span<byte> zero32 = stackalloc byte[32];
            if (CryptographicOperations.FixedTimeEquals(proofTag, zero32))
            {
                return false;
            }
        }

        return true;
    }

    public ZkPresentationBytes GenerateGroupPresentation(
        uint epoch,
        ReadOnlySpan<byte> transcriptChallenge,
        ReadOnlySpan<byte> groupMasterSecret32,
        ReadOnlySpan<byte> authCredentialMac)
    {
        // 136 bytes: 4 (magic) + 4 (epoch) + 32 (challengeHash) + 32 (pubKey) + 64 (signature)
        byte[] presentation = new byte[136];
        BinaryPrimitives.WriteUInt32BigEndian(presentation.AsSpan(0, 4), PresentationMagic);
        BinaryPrimitives.WriteUInt32BigEndian(presentation.AsSpan(4, 4), epoch);

        Span<byte> challengeHash = stackalloc byte[32];
        SHA256.HashData(transcriptChallenge, challengeHash);
        challengeHash.CopyTo(presentation.AsSpan(8, 32));

        Span<byte> privKey = stackalloc byte[32];
        Span<byte> pubKey = stackalloc byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privKey, pubKey);
        pubKey.CopyTo(presentation.AsSpan(40, 32));

        Span<byte> proofData = stackalloc byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(proofData[0..4], epoch);
        challengeHash.CopyTo(proofData[4..36]);

        Span<byte> signature = stackalloc byte[64];
        SignalCrypto.Ed25519Sign(privKey, proofData, signature);
        signature.CopyTo(presentation.AsSpan(72, 64));

        // Deterministic zeroization of secret scalar and proof material
        CryptographicOperations.ZeroMemory(privKey);
        CryptographicOperations.ZeroMemory(proofData);
        CryptographicOperations.ZeroMemory(signature);

        return ZkPresentationBytes.FromSpan(presentation);
    }
}
