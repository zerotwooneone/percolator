namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqliteGroupReceiverSessionRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteGroupReceiverSessionRepository _repository = null!;
    private FakeCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteGroupReceiverSessionRepository(_testDb.DbContext);
        _cryptoEngine = new FakeCryptoEngine();
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveReceiverSessionAsync_PersistsAndReloadsInFreshContext()
    {
        // Arrange
        var channelId = ChannelId.New();
        var authorId = PublicIdentityId.New();
        var deviceId = new DeviceId(2);

        var chainBytes = new byte[32];
        RandomNumberGenerator.Fill(chainBytes);
        using var chainKey = ChainKey.FromSpan(chainBytes);

        var session = new GroupReceiverSession(
            channelId,
            authorId,
            deviceId,
            chainKey,
            initialIteration: 0,
            keyId: 5);

        // Act
        await _repository.SaveReceiverSessionAsync(session);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteGroupReceiverSessionRepository(newContext);
        var loaded = await newRepo.GetReceiverSessionAsync(channelId, authorId, deviceId);

        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be(channelId);
        loaded.AuthorId.Should().Be(authorId);
        loaded.AuthorDeviceId.Should().Be(deviceId);
        loaded.KeyId.Should().Be(5);
        loaded.ReceivingCounter.Should().Be(0);

        var snapshot = loaded.ExportSnapshot();
        snapshot.ChainKeyBytes.Should().Equal(chainBytes);
    }

    [Test]
    public async Task SaveReceiverSessionAsync_WithSkippedKeys_PreservesSkippedKeysAcrossReload()
    {
        // Arrange
        var channelId = ChannelId.New();
        var authorId = PublicIdentityId.New();
        var deviceId = new DeviceId(1);

        var chainBytes = new byte[32];
        RandomNumberGenerator.Fill(chainBytes);
        using var chainKey = ChainKey.FromSpan(chainBytes);

        var session = new GroupReceiverSession(
            channelId,
            authorId,
            deviceId,
            chainKey,
            initialIteration: 0,
            keyId: 1);

        // Advance to iteration 3 (skips iterations 0, 1, 2)
        var advanceResult = session.TryAdvanceToIteration(3, _cryptoEngine);
        advanceResult.IsSuccess.Should().BeTrue();
        session.SkippedKeyCount.Should().Be(3);

        // Act - Save with skipped keys
        await _repository.SaveReceiverSessionAsync(session);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteGroupReceiverSessionRepository(newContext);
        var loaded = await newRepo.GetReceiverSessionAsync(channelId, authorId, deviceId);

        loaded.Should().NotBeNull();
        loaded!.SkippedKeyCount.Should().Be(3);
        loaded.ReceivingCounter.Should().Be(4);

        // Rehydrated session can retrieve a skipped key (e.g. iteration 1)
        var retrievedKeyResult = loaded.TryAdvanceToIteration(1, _cryptoEngine);
        retrievedKeyResult.IsSuccess.Should().BeTrue();
        retrievedKeyResult.Value.Should().NotBeNull();
        loaded.SkippedKeyCount.Should().Be(2); // One skipped key consumed
    }

    private sealed class FakeCryptoEngine : ICryptoEngine
    {
        public (ChainKey NextChainKey, MessageKey DerivedMessageKey) StepRatchet(ChainKey currentChainKey)
        {
            var nextChainBytes = new byte[32];
            currentChainKey.Span.CopyTo(nextChainBytes);
            nextChainBytes[0]++;

            var msgKeyBytes = new byte[32];
            currentChainKey.Span.CopyTo(msgKeyBytes);
            msgKeyBytes[31]++;

            return (ChainKey.FromSpan(nextChainBytes), MessageKey.FromSpan(msgKeyBytes));
        }

        public (ChainKey NextRootKey, ChainKey DerivedChainKey) KdfRk(ChainKey currentRootKey, SharedSecret dhSecret)
            => (ChainKey.FromSpan(new byte[32]), ChainKey.FromSpan(new byte[32]));

        public SharedSecret ComputeDiffieHellman(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
            => SharedSecret.FromSpan(new byte[32]);

        public (EphemeralPrivateKey PrivateKey, DhPublicKey PublicKey) GenerateEphemeralKeyPair()
            => (EphemeralPrivateKey.FromSpan(new byte[32]), DhPublicKey.FromSpan(new byte[32]));

        public SharedSecret DeriveX3dhMasterSecret(ReadOnlySpan<byte> dh1, ReadOnlySpan<byte> dh2, ReadOnlySpan<byte> dh3, ReadOnlySpan<byte> dh4 = default)
            => SharedSecret.FromSpan(new byte[32]);

        public byte[] EncryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
            => plaintext.ToArray();

        public byte[] DecryptAesGcm(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
            => ciphertext.ToArray();

        public bool VerifyEd25519Signature(IdentityKey publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) => true;

        public byte[] SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message) => new byte[64];

        public byte[] HkdfSha256(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int outputLength)
            => new byte[outputLength];
    }
}
