using Percolator.Domain.Identities.Ports;

namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqlitePrivatePreKeyStoreTests
{
    private SqliteTestDb _testDb = null!;
    private SqlitePrivatePreKeyStore _store = null!;
    private Domain.Identities.PublicIdentityId _ownerId;
    private DeviceId _deviceId;

    [SetUp]
    public async Task SetUp()
    {
        _testDb = new SqliteTestDb();
        _store = new SqlitePrivatePreKeyStore(_testDb.ContextFactory);
        _ownerId = Domain.Identities.PublicIdentityId.New();
        _deviceId = DeviceId.Primary;

        // Satisfy foreign key to vault_identity_keys
        await _testDb.DbContext.VaultIdentityKeys.AddAsync(new VaultIdentityKeyEntity
        {
            Id = _ownerId.Value,
            PublicKey = new byte[32],
            PrivateKey = new byte[32],
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await _testDb.DbContext.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task MarkConsumed_WipesPrivateKeyAndUpdatesOpkStatus()
    {
        // Arrange
        uint keyId = 101;
        byte[] privBytes = new byte[32];
        Array.Fill(privBytes, (byte)0x77);
        var opk = EphemeralPrivateKey.FromSpan(privBytes);

        await _store.StoreOneTimePreKeysAsync(_ownerId, _deviceId, new[]
        {
            new OneTimePreKeyMaterial(keyId, opk, DhPublicKey.FromSpan(new byte[32]))
        });

        // Act - Consume the OPK
        var consumedKey = await _store.TryConsumeOneTimePreKeyPrivateAsync(_ownerId, _deviceId, keyId);

        // Assert
        consumedKey.Should().NotBeNull();
        consumedKey!.Span.ToArray().Should().Equal(privBytes);

        // Subsequent attempt must return null (already consumed and wiped)
        var secondAttempt = await _store.TryConsumeOneTimePreKeyPrivateAsync(_ownerId, _deviceId, keyId);
        secondAttempt.Should().BeNull();

        // Direct DB inspection confirms private key is scrubbed to empty
        using var newContext = _testDb.CreateNewContext();
        var entity = await newContext.VaultOneTimePreKeys.FindAsync(_ownerId.Value, _deviceId.Value, keyId);
        entity.Should().NotBeNull();
        entity!.IsConsumed.Should().BeTrue();
        entity.PrivateKey.Should().BeEmpty();
    }

    [Test]
    public async Task StoreAndRetrieve_SignedPreKeyPrivate_RoundtripsSuccessfully()
    {
        // Arrange
        byte[] privBytes = new byte[32];
        Array.Fill(privBytes, (byte)0x88);
        var privKey = EphemeralPrivateKey.FromSpan(privBytes);

        // Act
        await _store.StoreSignedPreKeyAsync(
            _ownerId, _deviceId, privKey,
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));
        var retrieved = await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Span.ToArray().Should().Equal(privBytes);
    }

    [Test]
    public async Task StoreSignedPreKey_Twice_AssignsMonotonicIds_AndDemotesPreviousCurrent()
    {
        var first = await _store.StoreSignedPreKeyAsync(
            _ownerId, _deviceId,
            EphemeralPrivateKey.FromSpan(new byte[32]),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));

        var secondPrivBytes = new byte[32];
        Array.Fill(secondPrivBytes, (byte)0x99);
        var second = await _store.StoreSignedPreKeyAsync(
            _ownerId, _deviceId,
            EphemeralPrivateKey.FromSpan(secondPrivBytes),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));

        first.Value.Should().Be(1u);
        second.Value.Should().Be(2u);

        // GetSignedPreKeyAsync returns the current generation (second).
        var current = await _store.GetSignedPreKeyAsync(_ownerId, _deviceId);
        current.Should().NotBeNull();
        current!.KeyId.Value.Should().Be(2u);

        // Default private lookup returns the current (second) generation's key.
        var retrieved = await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId);
        retrieved.Should().NotBeNull();
        retrieved!.Span.ToArray().Should().Equal(secondPrivBytes);
    }

    [Test]
    public async Task GetSignedPreKeyPrivate_ByKeyId_ReturnsThatGeneration()
    {
        var gen1Priv = new byte[32];
        Array.Fill(gen1Priv, (byte)0x11);
        var gen2Priv = new byte[32];
        Array.Fill(gen2Priv, (byte)0x22);

        await _store.StoreSignedPreKeyAsync(
            _ownerId, _deviceId,
            EphemeralPrivateKey.FromSpan(gen1Priv),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));
        await _store.StoreSignedPreKeyAsync(
            _ownerId, _deviceId,
            EphemeralPrivateKey.FromSpan(gen2Priv),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));

        var gen1 = await _store.GetSignedPreKeyPrivateAsync(
            _ownerId, _deviceId, new SignedPreKeyId(1));
        gen1.Should().NotBeNull();
        gen1!.Span.ToArray().Should().Equal(gen1Priv);

        var unknown = await _store.GetSignedPreKeyPrivateAsync(
            _ownerId, _deviceId, new SignedPreKeyId(77));
        unknown.Should().BeNull();
    }

    [Test]
    public async Task PruneSignedPreKeys_RemovesAgedGenerations_ButRetainsMinimum_AndNeverCurrent()
    {
        // Five generations: ids 1..5, current = 5.
        for (int i = 0; i < 5; i++)
        {
            await _store.StoreSignedPreKeyAsync(
                _ownerId, _deviceId,
                EphemeralPrivateKey.FromSpan(new byte[32]),
                DhPublicKey.FromSpan(new byte[32]),
                DeviceLinkProof.FromSpan(new byte[64]));
        }

        // Everything is "old" relative to a future cutoff; minRetainedCount=2 keeps
        // the two newest (4, 5 — and 5 is current anyway).
        var deleted = await _store.PruneSignedPreKeysAsync(
            _ownerId, _deviceId, DateTimeOffset.UtcNow.AddMinutes(1), minRetainedCount: 2);

        deleted.Should().Be(3);
        (await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId, new SignedPreKeyId(1))).Should().BeNull();
        (await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId, new SignedPreKeyId(2))).Should().BeNull();
        (await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId, new SignedPreKeyId(3))).Should().BeNull();
        (await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId, new SignedPreKeyId(4))).Should().NotBeNull();
        (await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId, new SignedPreKeyId(5))).Should().NotBeNull();
    }

    [Test]
    public async Task CountAvailableOneTimePreKeys_ExcludesConsumedKeys()
    {
        await _store.StoreOneTimePreKeysAsync(_ownerId, _deviceId, new[]
        {
            new OneTimePreKeyMaterial(1u, EphemeralPrivateKey.FromSpan(new byte[32]), DhPublicKey.FromSpan(new byte[32])),
            new OneTimePreKeyMaterial(2u, EphemeralPrivateKey.FromSpan(new byte[32]), DhPublicKey.FromSpan(new byte[32])),
            new OneTimePreKeyMaterial(3u, EphemeralPrivateKey.FromSpan(new byte[32]), DhPublicKey.FromSpan(new byte[32]))
        });

        (await _store.CountAvailableOneTimePreKeysAsync(_ownerId, _deviceId)).Should().Be(3);

        await _store.TryConsumeOneTimePreKeyPrivateAsync(_ownerId, _deviceId, 1u);

        (await _store.CountAvailableOneTimePreKeysAsync(_ownerId, _deviceId)).Should().Be(2);
    }
}
