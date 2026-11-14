namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteLocalIdentityKeyStoreTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteLocalIdentityKeyStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _store = new SqliteLocalIdentityKeyStore(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task StoreAndRetrieveIdentity_MatchesKey()
    {
        // Arrange
        var identityId = Domain.Identities.PublicIdentityId.New();
        byte[] pubBytes = new byte[32];
        Array.Fill(pubBytes, (byte)0x11);
        byte[] privBytes = new byte[32];
        Array.Fill(privBytes, (byte)0x22);

        var pubKey = IdentityKey.FromSpan(pubBytes);
        using var privKey = EphemeralPrivateKey.FromSpan(privBytes);
        using var profileKey = ProfileKey.Generate();

        // Act
        await _store.SaveIdentityKeysAsync(identityId, privKey, pubKey, profileKey);

        // Assert
        var retrievedPub = await _store.GetIdentityPublicKeyAsync(identityId);
        retrievedPub.Should().NotBeNull();
        retrievedPub!.Span.ToArray().Should().Equal(pubBytes);

        using var retrievedPriv = await _store.GetIdentityPrivateKeyAsync(identityId);
        retrievedPriv.Should().NotBeNull();
        retrievedPriv!.Span.ToArray().Should().Equal(privBytes);

        var retrievedProfileKey = await _store.GetProfileKeyAsync(identityId);
        retrievedProfileKey.Should().NotBeNull();
        retrievedProfileKey!.Span.ToArray().Should().Equal(profileKey.Span.ToArray());
    }

    [Test]
    public async Task RotateProfileKeyAsync_ReplacesStoredProfileKey()
    {
        var identityId = Domain.Identities.PublicIdentityId.New();
        using var privKey = EphemeralPrivateKey.FromSpan(new byte[32]);
        using var profileKey = ProfileKey.Generate();
        await _store.SaveIdentityKeysAsync(identityId, privKey, IdentityKey.FromSpan(new byte[32]), profileKey);

        var rotated = await _store.RotateProfileKeyAsync(identityId);

        rotated.Span.ToArray().Should().NotEqual(profileKey.Span.ToArray());
        var persisted = await _store.GetProfileKeyAsync(identityId);
        persisted!.Span.ToArray().Should().Equal(rotated.Span.ToArray());
    }
}
