namespace Percolator.Infrastructure.Vault;

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Konscious.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

internal sealed class VaultEnvelopePacker : IVaultEnvelopePacker
{
    private static readonly byte[] DuressSentinelBytes =
        SHA256.HashData(Encoding.UTF8.GetBytes("PERCOLATOR_VAULT_DURESS_SENTINEL_V1"));

    private readonly int _defaultMemoryKb;
    private readonly int _defaultIterations;
    private readonly int _defaultParallelism;

    public VaultEnvelopePacker(int memoryKb = 65536, int iterations = 3, int parallelism = 4)
    {
        _defaultMemoryKb = memoryKb;
        _defaultIterations = iterations;
        _defaultParallelism = parallelism;
    }

    public DomainResult<DatabaseHeader> CreateEnvelope(
        VaultPassphrase masterPassword,
        VaultPassphrase? duressPassword,
        ReadOnlySpan<byte> databaseKeySpan)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        if (databaseKeySpan.Length != 32)
        {
            return DomainResult<DatabaseHeader>.Failure(new DomainError("VAULT_INVALID_KEY", "Database key must be 32 bytes."));
        }

        if (duressPassword != null && !duressPassword.IsEmpty && VaultPassphrase.ConstantTimeEquals(masterPassword, duressPassword))
        {
            return DomainResult<DatabaseHeader>.Failure(new DomainError("VAULT_KEY_COLLISION", "Duress password cannot match master password."));
        }

        // 1. Pack Master Slot
        byte[] masterSalt = RandomNumberGenerator.GetBytes(16);
        byte[] masterNonce = RandomNumberGenerator.GetBytes(12);
        byte[] derivedMasterKey = DeriveArgon2idKey(
            masterPassword,
            masterSalt,
            _defaultMemoryKb,
            _defaultIterations,
            _defaultParallelism);

        byte[] masterPayload = new byte[48];
        try
        {
            using var cipher = new ChaCha20Poly1305(derivedMasterKey);
            cipher.Encrypt(
                masterNonce,
                databaseKeySpan,
                masterPayload.AsSpan(0, 32),
                masterPayload.AsSpan(32, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedMasterKey);
        }

        // 2. Pack Duress Slot
        byte[] duressSalt;
        byte[] duressNonce;
        byte[] duressPayload;

        if (duressPassword != null && !duressPassword.IsEmpty)
        {
            duressSalt = RandomNumberGenerator.GetBytes(16);
            duressNonce = RandomNumberGenerator.GetBytes(12);
            byte[] derivedDuressKey = DeriveArgon2idKey(
                duressPassword,
                duressSalt,
                _defaultMemoryKb,
                _defaultIterations,
                _defaultParallelism);

            duressPayload = new byte[48];
            try
            {
                using var cipher = new ChaCha20Poly1305(derivedDuressKey);
                cipher.Encrypt(
                    duressNonce,
                    DuressSentinelBytes,
                    duressPayload.AsSpan(0, 32),
                    duressPayload.AsSpan(32, 16));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedDuressKey);
            }
        }
        else
        {
            // Plausible deniability CSPRNG noise (76 bytes total)
            duressSalt = RandomNumberGenerator.GetBytes(16);
            duressNonce = RandomNumberGenerator.GetBytes(12);
            duressPayload = RandomNumberGenerator.GetBytes(48);
        }

        var header = new DatabaseHeader
        {
            Version = 1,
            Argon2MemoryKb = _defaultMemoryKb,
            Argon2Iterations = _defaultIterations,
            Argon2Parallelism = _defaultParallelism,
            MasterSlotSalt = ByteString.CopyFrom(masterSalt),
            MasterSlotNonce = ByteString.CopyFrom(masterNonce),
            MasterSlotPayload = ByteString.CopyFrom(masterPayload),
            DuressSlotSalt = ByteString.CopyFrom(duressSalt),
            DuressSlotNonce = ByteString.CopyFrom(duressNonce),
            DuressSlotPayload = ByteString.CopyFrom(duressPayload)
        };

        return DomainResult<DatabaseHeader>.Success(header);
    }

    public SlotTrialResult EvaluateSlots(
        DatabaseHeader header,
        VaultPassphrase candidatePassword,
        Span<byte> destinationDatabaseKeyBuffer)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(candidatePassword);

        if (destinationDatabaseKeyBuffer.Length < 32)
        {
            throw new ArgumentException("Destination database key buffer must be at least 32 bytes.", nameof(destinationDatabaseKeyBuffer));
        }

        // 1. Evaluate Master Slot
        byte[] derivedMasterCandidateKey = DeriveArgon2idKey(
            candidatePassword,
            header.MasterSlotSalt.Span,
            header.Argon2MemoryKb,
            header.Argon2Iterations,
            header.Argon2Parallelism);

        try
        {
            using var cipher = new ChaCha20Poly1305(derivedMasterCandidateKey);
            Span<byte> candidatePlaintext = stackalloc byte[32];
            try
            {
                cipher.Decrypt(
                    header.MasterSlotNonce.Span,
                    header.MasterSlotPayload.Span[..32],
                    header.MasterSlotPayload.Span[32..],
                    candidatePlaintext);

                candidatePlaintext.CopyTo(destinationDatabaseKeyBuffer);
                CryptographicOperations.ZeroMemory(candidatePlaintext);
                return new SlotTrialResult(SlotTrialOutcome.MasterUnlocked);
            }
            catch (AuthenticationTagMismatchException)
            {
                // Tag mismatch: candidate key is not the master key
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedMasterCandidateKey);
        }

        // 2. Evaluate Duress Slot
        byte[] derivedDuressCandidateKey = DeriveArgon2idKey(
            candidatePassword,
            header.DuressSlotSalt.Span,
            header.Argon2MemoryKb,
            header.Argon2Iterations,
            header.Argon2Parallelism);

        try
        {
            using var cipher = new ChaCha20Poly1305(derivedDuressCandidateKey);
            Span<byte> candidatePlaintext = stackalloc byte[32];
            try
            {
                cipher.Decrypt(
                    header.DuressSlotNonce.Span,
                    header.DuressSlotPayload.Span[..32],
                    header.DuressSlotPayload.Span[32..],
                    candidatePlaintext);

                if (CryptographicOperations.FixedTimeEquals(candidatePlaintext, DuressSentinelBytes))
                {
                    CryptographicOperations.ZeroMemory(candidatePlaintext);
                    return new SlotTrialResult(SlotTrialOutcome.DuressTriggered);
                }
            }
            catch (AuthenticationTagMismatchException)
            {
                // Active duress tag mismatch OR dummy random bytes failed authentication
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedDuressCandidateKey);
        }

        return new SlotTrialResult(SlotTrialOutcome.InvalidCredentials);
    }

    public DomainResult<DatabaseHeader> RewrapMasterSlot(
        DatabaseHeader currentHeader,
        VaultPassphrase newPassword,
        ReadOnlySpan<byte> databaseKeySpan)
    {
        ArgumentNullException.ThrowIfNull(currentHeader);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (databaseKeySpan.Length != 32)
        {
            return DomainResult<DatabaseHeader>.Failure(new DomainError("VAULT_INVALID_KEY", "Database key must be 32 bytes."));
        }

        byte[] newMasterSalt = RandomNumberGenerator.GetBytes(16);
        byte[] newMasterNonce = RandomNumberGenerator.GetBytes(12);
        byte[] derivedMasterKey = DeriveArgon2idKey(
            newPassword,
            newMasterSalt,
            currentHeader.Argon2MemoryKb,
            currentHeader.Argon2Iterations,
            currentHeader.Argon2Parallelism);

        byte[] newMasterPayload = new byte[48];
        try
        {
            using var cipher = new ChaCha20Poly1305(derivedMasterKey);
            cipher.Encrypt(
                newMasterNonce,
                databaseKeySpan,
                newMasterPayload.AsSpan(0, 32),
                newMasterPayload.AsSpan(32, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedMasterKey);
        }

        var updatedHeader = new DatabaseHeader
        {
            Version = currentHeader.Version,
            Argon2MemoryKb = currentHeader.Argon2MemoryKb,
            Argon2Iterations = currentHeader.Argon2Iterations,
            Argon2Parallelism = currentHeader.Argon2Parallelism,
            MasterSlotSalt = ByteString.CopyFrom(newMasterSalt),
            MasterSlotNonce = ByteString.CopyFrom(newMasterNonce),
            MasterSlotPayload = ByteString.CopyFrom(newMasterPayload),
            DuressSlotSalt = currentHeader.DuressSlotSalt,
            DuressSlotNonce = currentHeader.DuressSlotNonce,
            DuressSlotPayload = currentHeader.DuressSlotPayload
        };

        return DomainResult<DatabaseHeader>.Success(updatedHeader);
    }

    public DomainResult<DatabaseHeader> ConfigureDuressSlot(
        DatabaseHeader currentHeader,
        VaultPassphrase? duressPassword)
    {
        ArgumentNullException.ThrowIfNull(currentHeader);

        byte[] duressSalt;
        byte[] duressNonce;
        byte[] duressPayload;

        if (duressPassword != null && !duressPassword.IsEmpty)
        {
            duressSalt = RandomNumberGenerator.GetBytes(16);
            duressNonce = RandomNumberGenerator.GetBytes(12);
            byte[] derivedDuressKey = DeriveArgon2idKey(
                duressPassword,
                duressSalt,
                currentHeader.Argon2MemoryKb,
                currentHeader.Argon2Iterations,
                currentHeader.Argon2Parallelism);

            duressPayload = new byte[48];
            try
            {
                using var cipher = new ChaCha20Poly1305(derivedDuressKey);
                cipher.Encrypt(
                    duressNonce,
                    DuressSentinelBytes,
                    duressPayload.AsSpan(0, 32),
                    duressPayload.AsSpan(32, 16));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedDuressKey);
            }
        }
        else
        {
            duressSalt = RandomNumberGenerator.GetBytes(16);
            duressNonce = RandomNumberGenerator.GetBytes(12);
            duressPayload = RandomNumberGenerator.GetBytes(48);
        }

        var updatedHeader = new DatabaseHeader
        {
            Version = currentHeader.Version,
            Argon2MemoryKb = currentHeader.Argon2MemoryKb,
            Argon2Iterations = currentHeader.Argon2Iterations,
            Argon2Parallelism = currentHeader.Argon2Parallelism,
            MasterSlotSalt = currentHeader.MasterSlotSalt,
            MasterSlotNonce = currentHeader.MasterSlotNonce,
            MasterSlotPayload = currentHeader.MasterSlotPayload,
            DuressSlotSalt = ByteString.CopyFrom(duressSalt),
            DuressSlotNonce = ByteString.CopyFrom(duressNonce),
            DuressSlotPayload = ByteString.CopyFrom(duressPayload)
        };

        return DomainResult<DatabaseHeader>.Success(updatedHeader);
    }

    private static byte[] DeriveArgon2idKey(
        VaultPassphrase passphrase,
        ReadOnlySpan<byte> salt,
        int memoryKb,
        int iterations,
        int parallelism)
    {
        byte[] saltArray = salt.ToArray();
        return passphrase.WithUtf8Span(utf8Span =>
        {
            // Konscious.Security.Cryptography.Argon2 throws if password array is 0-length.
            // When passphrase is empty, feed a 1-byte buffer [0x00] so Argon2 derives a valid key.
            byte[] passwordBytes = utf8Span.Length == 0 ? [0x00] : utf8Span.ToArray();
            using var argon2 = new Argon2id(passwordBytes);
            argon2.Salt = saltArray;
            argon2.MemorySize = memoryKb;
            argon2.Iterations = iterations;
            argon2.DegreeOfParallelism = parallelism;
            return argon2.GetBytes(32);
        });
    }
}
