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
    private PublicIdentityId _ownerId;
    private DeviceId _deviceId;

    [SetUp]
    public async Task SetUp()
    {
        _testDb = new SqliteTestDb();
        _store = new SqlitePrivatePreKeyStore(_testDb.DbContext);
        _ownerId = PublicIdentityId.New();
        _deviceId = DeviceId.Primary;

        // Satisfy foreign key to vault_identity_keys
        await _testDb.DbContext.VaultIdentityKeys.AddAsync(new VaultIdentityKeyEntity
        {
            Id = _ownerId.Value,
            PublicKey = new byte[32],
            PrivateKeyCiphertext = new byte[32],
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

        await _store.StoreOneTimePreKeysPrivateAsync(_ownerId, _deviceId, new[] { (keyId, opk) });

        // Act - Consume the OPK
        var consumedKey = await _store.TryConsumeOneTimePreKeyPrivateAsync(_ownerId, _deviceId, keyId);

        // Assert
        consumedKey.Should().NotBeNull();
        consumedKey!.Span.ToArray().Should().Equal(privBytes);

        // Subsequent attempt must return null (already consumed and wiped)
        var secondAttempt = await _store.TryConsumeOneTimePreKeyPrivateAsync(_ownerId, _deviceId, keyId);
        secondAttempt.Should().BeNull();

        // Direct DB inspection confirms private key ciphertext is scrubbed to empty
        using var newContext = _testDb.CreateNewContext();
        var entity = await newContext.VaultOneTimePreKeys.FindAsync(_ownerId.Value, _deviceId.Value, keyId);
        entity.Should().NotBeNull();
        entity!.IsConsumed.Should().BeTrue();
        entity.PrivateKeyCiphertext.Should().BeEmpty();
    }

    [Test]
    public async Task StoreAndRetrieve_SignedPreKeyPrivate_RoundtripsSuccessfully()
    {
        // Arrange
        byte[] privBytes = new byte[32];
        Array.Fill(privBytes, (byte)0x88);
        var privKey = EphemeralPrivateKey.FromSpan(privBytes);

        // Act
        await _store.StoreSignedPreKeyPrivateAsync(_ownerId, _deviceId, privKey);
        var retrieved = await _store.GetSignedPreKeyPrivateAsync(_ownerId, _deviceId);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Span.ToArray().Should().Equal(privBytes);
    }
}
