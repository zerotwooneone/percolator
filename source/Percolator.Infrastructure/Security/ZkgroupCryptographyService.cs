namespace Percolator.Infrastructure.Security;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

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

        return true;
    }

    public ZkPresentationBytes GenerateGroupPresentation(
        uint epoch,
        ReadOnlySpan<byte> transcriptChallenge,
        ReadOnlySpan<byte> groupMasterSecret32,
        ReadOnlySpan<byte> authCredentialMac)
    {
        byte[] presentation = new byte[72];
        BinaryPrimitives.WriteUInt32BigEndian(presentation.AsSpan(0, 4), PresentationMagic);
        BinaryPrimitives.WriteUInt32BigEndian(presentation.AsSpan(4, 4), epoch);

        Span<byte> challengeHash = stackalloc byte[32];
        SHA256.HashData(transcriptChallenge, challengeHash);
        challengeHash.CopyTo(presentation.AsSpan(8, 32));

        Span<byte> macKey = stackalloc byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            groupMasterSecret32,
            macKey,
            salt: authCredentialMac,
            info: "zkgroup-presentation-v1"u8);

        using var hmac = new HMACSHA256(macKey.ToArray());
        Span<byte> proofData = stackalloc byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(proofData[0..4], epoch);
        challengeHash.CopyTo(proofData[4..36]);

        byte[] tag = hmac.ComputeHash(proofData.ToArray());
        Buffer.BlockCopy(tag, 0, presentation, 40, 32);

        CryptographicOperations.ZeroMemory(macKey);
        return ZkPresentationBytes.FromSpan(presentation);
    }
}
