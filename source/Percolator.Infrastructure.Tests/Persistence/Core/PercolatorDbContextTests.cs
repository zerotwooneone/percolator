namespace Percolator.Infrastructure.Tests.Persistence.Core;

using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class PercolatorDbContextTests
{
    private SqliteTestDb _testDb = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task ColumnLevelEncryption_StoresKeysAsCiphertextAtRest()
    {
        // Arrange
        var keyId = Guid.NewGuid();
        byte[] rawPlaintextKey = Encoding.UTF8.GetBytes("SuperSecretIdentityPrivateKey123");
        byte[] publicKey = new byte[32];
        Array.Fill(publicKey, (byte)0x42);

        var entity = new VaultIdentityKeyEntity
        {
            Id = keyId,
            PublicKey = publicKey,
            PrivateKeyCiphertext = rawPlaintextKey,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _testDb.DbContext.VaultIdentityKeys.AddAsync(entity);
        await _testDb.DbContext.SaveChangesAsync();

        // Act - Direct raw ADO.NET query inspecting bytes at rest
        await using var cmd = _testDb.Connection.CreateCommand();
        cmd.CommandText = "SELECT private_key_ciphertext FROM vault_identity_keys WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", keyId);
        var storedBytes = (byte[]?)await cmd.ExecuteScalarAsync();

        // Assert - The stored bytes in SQLite must NOT contain the plaintext key string
        storedBytes.Should().NotBeNull();
        storedBytes!.Length.Should().Be(12 + 16 + rawPlaintextKey.Length);
        storedBytes.Should().NotEqual(rawPlaintextKey);
        Encoding.UTF8.GetString(storedBytes).Should().NotContain("SuperSecretIdentityPrivateKey123");

        // Decryption round-trip via new context instance with the same master key
        using var readContext = _testDb.CreateNewContext();
        var retrieved = await readContext.VaultIdentityKeys.FindAsync(keyId);
        retrieved.Should().NotBeNull();
        retrieved!.PrivateKeyCiphertext.Should().Equal(rawPlaintextKey);
    }

    [Test]
    public async Task CascadeDelete_RemovesChildEntities()
    {
        var ownerId = Guid.NewGuid();
        var identity = new IdentityProfileEntity
        {
            Id = ownerId,
            DisplayName = "Test Alice",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var device = new LocalDeviceEntity
        {
            OwnerIdentityId = ownerId,
            DeviceId = 1,
            DevicePublicKey = new byte[32],
            LinkProofSignature = new byte[64],
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _testDb.DbContext.IdentityProfiles.AddAsync(identity);
        await _testDb.DbContext.LocalDevices.AddAsync(device);
        await _testDb.DbContext.SaveChangesAsync();

        // Delete parent identity
        _testDb.DbContext.IdentityProfiles.Remove(identity);
        await _testDb.DbContext.SaveChangesAsync();

        using var readContext = _testDb.CreateNewContext();
        var devicesCount = await readContext.LocalDevices.CountAsync(d => d.OwnerIdentityId == ownerId);
        devicesCount.Should().Be(0);
    }
}
