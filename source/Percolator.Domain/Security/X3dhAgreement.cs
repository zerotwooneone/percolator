using System.Buffers.Binary;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Security;

public sealed record X3dhInitiatorResult(
    SharedSecret MasterSecret,
    DhPublicKey EphemeralPublicKey,
    IdentityKey InitiatorIdentityKey,
    uint? OneTimePreKeyIdUsed,
    EphemeralPrivateKey? EphemeralPrivateKey = null);

/// <summary>
/// Encapsulates the cryptographic rules for the Extended Triple Diffie-Hellman (X3DH) key agreement protocol.
/// Guarantees mutual authentication and forward secrecy prior to initial Double Ratchet establishment.
/// </summary>
public static class X3dhAgreement
{
    public static DomainResult<X3dhInitiatorResult> Initiate(
        ReadOnlySpan<byte> initiatorIdentityPrivateKey,
        IdentityKey initiatorIdentityPublicKey,
        PreKeyBundle remoteBundle,
        ICryptoEngine cryptoEngine)
    {
        if (initiatorIdentityPrivateKey.IsEmpty)
        {
            return DomainResult<X3dhInitiatorResult>.Failure(
                new DomainError("INVALID_IDENTITY_KEY", "Initiator identity private key cannot be empty."));
        }

        if (remoteBundle == null)
        {
            return DomainResult<X3dhInitiatorResult>.Failure(
                new DomainError("NULL_PREKEY_BUNDLE", "Remote pre-key bundle cannot be null."));
        }

        // 1. Verify remote signed pre-key signature using remote identity key
        if (!cryptoEngine.VerifyEd25519Signature(
            remoteBundle.IdentityKey,
            remoteBundle.SignedPreKey.Span,
            remoteBundle.SignedPreKeySignature.Span))
        {
            return DomainResult<X3dhInitiatorResult>.Failure(
                new DomainError("INVALID_SIGNED_PREKEY_SIGNATURE", "Remote signed prekey signature is invalid or forged."));
        }

        // 2. Generate ephemeral key pair
        var (ephemeralPrivateKey, ephemeralPublicKey) = cryptoEngine.GenerateEphemeralKeyPair();

        // 3. Compute Diffie-Hellman shared secrets:
        // DH1 = DH(IK_A, SPK_B)
        var dh1 = cryptoEngine.ComputeDiffieHellman(initiatorIdentityPrivateKey, remoteBundle.SignedPreKey.Span);

        // DH2 = DH(EK_A, IK_B)
        var dh2 = cryptoEngine.ComputeDiffieHellman(ephemeralPrivateKey.Span, remoteBundle.IdentityKey.Span);

        // DH3 = DH(EK_A, SPK_B)
        var dh3 = cryptoEngine.ComputeDiffieHellman(ephemeralPrivateKey.Span, remoteBundle.SignedPreKey.Span);

        // DH4 = DH(EK_A, OPK_B) [optional]
        SharedSecret? dh4 = null;
        uint? opkIdUsed = null;
        if (remoteBundle.OneTimePreKey != null)
        {
            dh4 = cryptoEngine.ComputeDiffieHellman(ephemeralPrivateKey.Span, remoteBundle.OneTimePreKey.Span);
            opkIdUsed = remoteBundle.OneTimePreKeyId;
        }

        ReadOnlySpan<byte> dh4Span = dh4 != null ? dh4.Span : default;

        // 4. Derive master shared secret
        var masterSecret = cryptoEngine.DeriveX3dhMasterSecret(
            dh1.Span,
            dh2.Span,
            dh3.Span,
            dh4Span);

        return DomainResult<X3dhInitiatorResult>.Success(
            new X3dhInitiatorResult(masterSecret, ephemeralPublicKey, initiatorIdentityPublicKey, opkIdUsed, ephemeralPrivateKey));
    }

    public static DomainResult<SharedSecret> Receive(
        ReadOnlySpan<byte> receiverIdentityPrivateKey,
        ReadOnlySpan<byte> receiverSignedPreKeyPrivateKey,
        ReadOnlySpan<byte> receiverOneTimePreKeyPrivateKeyOrEmpty,
        IdentityKey initiatorIdentityKey,
        DhPublicKey initiatorEphemeralKey,
        ICryptoEngine cryptoEngine)
    {
        if (receiverIdentityPrivateKey.IsEmpty || receiverSignedPreKeyPrivateKey.IsEmpty)
        {
            return DomainResult<SharedSecret>.Failure(
                new DomainError("INVALID_PRIVATE_KEYS", "Receiver identity and signed prekey private keys are required."));
        }

        if (initiatorIdentityKey == null || initiatorEphemeralKey == null)
        {
            return DomainResult<SharedSecret>.Failure(
                new DomainError("NULL_KEY_MATERIAL", "Initiator identity key and ephemeral key cannot be null."));
        }

        // DH1 = DH(SPK_B, IK_A)
        var dh1 = cryptoEngine.ComputeDiffieHellman(receiverSignedPreKeyPrivateKey, initiatorIdentityKey.Span);

        // DH2 = DH(IK_B, EK_A)
        var dh2 = cryptoEngine.ComputeDiffieHellman(receiverIdentityPrivateKey, initiatorEphemeralKey.Span);

        // DH3 = DH(SPK_B, EK_A)
        var dh3 = cryptoEngine.ComputeDiffieHellman(receiverSignedPreKeyPrivateKey, initiatorEphemeralKey.Span);

        // DH4 = DH(OPK_B, EK_A) [optional]
        SharedSecret? dh4 = null;
        if (!receiverOneTimePreKeyPrivateKeyOrEmpty.IsEmpty)
        {
            dh4 = cryptoEngine.ComputeDiffieHellman(receiverOneTimePreKeyPrivateKeyOrEmpty, initiatorEphemeralKey.Span);
        }

        ReadOnlySpan<byte> dh4Span = dh4 != null ? dh4.Span : default;

        var masterSecret = cryptoEngine.DeriveX3dhMasterSecret(
            dh1.Span,
            dh2.Span,
            dh3.Span,
            dh4Span);

        return DomainResult<SharedSecret>.Success(masterSecret);
    }
}
