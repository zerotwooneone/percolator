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
    public async Task SaveAndLoad_PreservesKeysAndState()
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

        // Assert - Direct raw DB inspection verifies root_key is stored directly as binary in SQLCipher store
        await using var cmd = _testDb.Connection.CreateCommand();
        cmd.CommandText = "SELECT root_key FROM vault_ratchet_sessions WHERE session_id = @sid;";
        cmd.Parameters.AddWithValue("@sid", session.Id.ToString());
        var rawBytes = (byte[]?)await cmd.ExecuteScalarAsync();

        rawBytes.Should().NotBeNull();
        rawBytes.Should().Equal(rootKeyBytes);

        // Act - Reload via repository
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteRatchetSessionRepository(newContext);
        var loaded = await newRepo.GetSessionAsync(ownerId, remoteId, remoteDevice);

        // Assert - Reloaded roundtrip matches original session state
        loaded.Should().NotBeNull();
        loaded!.OwnerIdentityId.Should().Be(ownerId);
        loaded.RemotePeerId.Should().Be(remoteId);
        loaded.SendingCounter.Should().Be(5);
        loaded.ReceivingCounter.Should().Be(2);
        var snapshot = loaded.ExportSnapshot();
        snapshot.RootKeyBytes.Should().Equal(rootKeyBytes);
        snapshot.SendingChainKeyBytes.Should().Equal(sendKeyBytes);
    }

    [Test]
    public async Task SaveSessionAsync_SupersededSession_ReplacesPreviousSessionOnSameRouteWithoutUniqueConstraintViolation()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var ownerDevice = DeviceId.Primary;
        var remoteDevice = new DeviceId(2);

        byte[] rootKeyBytes1 = new byte[32];
        Array.Fill(rootKeyBytes1, (byte)0x11);
        var session1 = new DirectRatchetSession(
            ownerId,
            ownerDevice,
            remoteId,
            remoteDevice,
            ChainKey.FromSpan(rootKeyBytes1),
            sendingChainKey: null,
            receivingChainKey: null,
            remoteEphemeralPublicKey: null,
            localEphemeralPrivateKey: null,
            localEphemeralPublicKey: null);

        await _repository.SaveSessionAsync(session1);

        // Act - Create and save session2 on the identical route (same owner, ownerDevice, remote, remoteDevice)
        byte[] rootKeyBytes2 = new byte[32];
        Array.Fill(rootKeyBytes2, (byte)0x22);
        var session2 = new DirectRatchetSession(
            ownerId,
            ownerDevice,
            remoteId,
            remoteDevice,
            ChainKey.FromSpan(rootKeyBytes2),
            sendingChainKey: null,
            receivingChainKey: null,
            remoteEphemeralPublicKey: null,
            localEphemeralPrivateKey: null,
            localEphemeralPublicKey: null);

        // This must not throw SQLite UNIQUE constraint violation
        var act = async () => await _repository.SaveSessionAsync(session2);
        await act.Should().NotThrowAsync();

        // Assert - Only session2 exists for this route
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteRatchetSessionRepository(newContext);
        var loaded = await newRepo.GetSessionAsync(ownerId, remoteId, remoteDevice);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(session2.Id);
        loaded.ExportSnapshot().RootKeyBytes.Should().Equal(rootKeyBytes2);
    }
}
