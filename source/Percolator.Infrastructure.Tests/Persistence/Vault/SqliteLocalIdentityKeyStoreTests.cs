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
        var identityId = PublicIdentityId.New();
        byte[] pubBytes = new byte[32];
        Array.Fill(pubBytes, (byte)0x11);
        byte[] privBytes = new byte[32];
        Array.Fill(privBytes, (byte)0x22);

        var pubKey = IdentityKey.FromSpan(pubBytes);

        // Act
        await _store.SaveKeyAsync(identityId, privBytes, pubKey);

        // Assert
        var retrievedPub = await _store.GetIdentityPublicKeyAsync(identityId);
        retrievedPub.Should().NotBeNull();
        retrievedPub!.Span.ToArray().Should().Equal(pubBytes);

        var retrievedPriv = await _store.GetIdentityPrivateKeyAsync(identityId);
        retrievedPriv.Should().NotBeNull();
        retrievedPriv.Should().Equal(privBytes);
    }
}
