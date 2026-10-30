namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteRatchetSessionRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteRatchetSessionRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteRatchetSessionRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveAndLoad_PreservesCiphertextAndState()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var ownerDevice = DeviceId.Primary;
        var remoteDevice = new DeviceId(2);

        byte[] rootKeyBytes = new byte[32];
        Array.Fill(rootKeyBytes, (byte)0x33);
        byte[] sendKeyBytes = new byte[32];
        Array.Fill(sendKeyBytes, (byte)0x44);

        var session = new DirectRatchetSession(
            ownerId,
            ownerDevice,
            remoteId,
            remoteDevice,
            ChainKey.FromSpan(rootKeyBytes),
            ChainKey.FromSpan(sendKeyBytes),
            receivingChainKey: null,
            remoteEphemeralPublicKey: null,
            localEphemeralPrivateKey: null,
            localEphemeralPublicKey: null,
            sendingCounter: 5,
            receivingCounter: 2);

        // Act - Save session
        await _repository.SaveSessionAsync(session);

        // Assert - Direct raw DB inspection verifies root_key_ciphertext is encrypted at rest
        await using var cmd = _testDb.Connection.CreateCommand();
        cmd.CommandText = "SELECT root_key_ciphertext FROM vault_ratchet_sessions WHERE session_id = @sid;";
        cmd.Parameters.AddWithValue("@sid", session.Id.ToString());
        var rawBytes = (byte[]?)await cmd.ExecuteScalarAsync();

        rawBytes.Should().NotBeNull();
        rawBytes!.Length.Should().Be(12 + 16 + 32); // [Nonce 12B][Tag 16B][Ciphertext 32B]
        rawBytes.Should().NotEqual(rootKeyBytes);

        // Act - Reload via repository
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteRatchetSessionRepository(newContext);
        var loaded = await newRepo.GetSessionAsync(ownerId, remoteId, remoteDevice);

        // Assert - Decrypted roundtrip matches original session state
        loaded.Should().NotBeNull();
        loaded!.OwnerIdentityId.Should().Be(ownerId);
        loaded.RemotePeerId.Should().Be(remoteId);
        loaded.SendingCounter.Should().Be(5);
        loaded.ReceivingCounter.Should().Be(2);
        loaded.RootKey.Should().NotBeNull();
        loaded.RootKey!.Span.ToArray().Should().Equal(rootKeyBytes);
        loaded.SendingChainKey.Should().NotBeNull();
        loaded.SendingChainKey!.Span.ToArray().Should().Equal(sendKeyBytes);
    }
}
