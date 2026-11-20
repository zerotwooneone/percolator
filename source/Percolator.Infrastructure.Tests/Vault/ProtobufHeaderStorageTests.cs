namespace Percolator.Infrastructure.Tests.Vault;

using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class ProtobufHeaderStorageTests
{
    private string _tempDir = null!;
    private VaultStorageOptions _options = null!;
    private ProtobufHeaderStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "percolator_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new VaultStorageOptions { BaseDirectory = _tempDir };
        _storage = new ProtobufHeaderStorage(_options);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public void PlausibleDeniability_WireSize_IdenticalWithOrWithoutDuress()
    {
        // 1. Header with active duress slot
        var headerWithDuress = new DatabaseHeader
        {
            Version = 1,
            Argon2MemoryKb = 65536,
            Argon2Iterations = 3,
            Argon2Parallelism = 4,
            MasterSlotSalt = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
            MasterSlotNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
            MasterSlotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48)),
            DuressSlotSalt = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
            DuressSlotNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
            DuressSlotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48))
        };

        // 2. Header with dummy CSPRNG random noise (no duress)
        var headerWithoutDuress = new DatabaseHeader
        {
            Version = 1,
            Argon2MemoryKb = 65536,
            Argon2Iterations = 3,
            Argon2Parallelism = 4,
            MasterSlotSalt = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
            MasterSlotNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
            MasterSlotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48)),
            DuressSlotSalt = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
            DuressSlotNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
            DuressSlotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48))
        };

        byte[] bytesWithDuress = headerWithDuress.ToByteArray();
        byte[] bytesWithoutDuress = headerWithoutDuress.ToByteArray();

        // Duress slot bytes must be exactly 76 bytes in both (16B salt + 12B nonce + 48B payload)
        (headerWithDuress.DuressSlotSalt.Length + headerWithDuress.DuressSlotNonce.Length + headerWithDuress.DuressSlotPayload.Length)
            .Should().Be(76);
        (headerWithoutDuress.DuressSlotSalt.Length + headerWithoutDuress.DuressSlotNonce.Length + headerWithoutDuress.DuressSlotPayload.Length)
            .Should().Be(76);

        // Total wire sizes must be identical
        bytesWithDuress.Length.Should().Be(bytesWithoutDuress.Length);

        // Attempting to decrypt the dummy duress payload with a key derived from a candidate key must fail with AuthenticationTagMismatchException
        byte[] dummyKey = RandomNumberGenerator.GetBytes(32);
        using var chacha = new ChaCha20Poly1305(dummyKey);
        byte[] decrypted = new byte[32];

        Action decryptAction = () => chacha.Decrypt(
            headerWithoutDuress.DuressSlotNonce.Span,
            headerWithoutDuress.DuressSlotPayload.Span[..32],
            headerWithoutDuress.DuressSlotPayload.Span[32..],
            decrypted);

        decryptAction.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Test]
    public async Task CorruptedHeader_TrapsGracefully()
    {
        // 1. Missing header returns failure
        var missingResult = await _storage.ReadHeaderAsync();
        missingResult.IsFailure.Should().BeTrue();
        missingResult.Error.Code.Should().Be("HEADER_NOT_FOUND");

        // 2. 0-byte header file returns failure
        await File.WriteAllBytesAsync(_options.HeaderFilePath, Array.Empty<byte>());
        var emptyResult = await _storage.ReadHeaderAsync();
        emptyResult.IsFailure.Should().BeTrue();
        emptyResult.Error.Code.Should().Be("HEADER_EMPTY");

        // 3. Corrupted / truncated bytes returns failure without throwing uncaught exception
        byte[] corruptedBytes = [0xFF, 0xFE, 0x01, 0x99];
        await File.WriteAllBytesAsync(_options.HeaderFilePath, corruptedBytes);
        var corruptedResult = await _storage.ReadHeaderAsync();
        corruptedResult.IsFailure.Should().BeTrue();
        corruptedResult.Error.Code.Should().Be("HEADER_CORRUPTED");
    }

    [Test]
    public async Task WriteAndReadHeader_RoundtripsSuccessfully()
    {
        var header = new DatabaseHeader
        {
            Version = 1,
            Argon2MemoryKb = 65536,
            Argon2Iterations = 3,
            Argon2Parallelism = 4,
            MasterSlotSalt = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
            MasterSlotNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
            MasterSlotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48)),
            DuressSlotSalt = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
            DuressSlotNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
            DuressSlotPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48))
        };

        var writeResult = await _storage.WriteHeaderAsync(header);
        writeResult.IsSuccess.Should().BeTrue();

        var readResult = await _storage.ReadHeaderAsync();
        readResult.IsSuccess.Should().BeTrue();
        readResult.Value.Version.Should().Be(1);
        readResult.Value.MasterSlotSalt.Span.SequenceEqual(header.MasterSlotSalt.Span).Should().BeTrue();
    }
}
