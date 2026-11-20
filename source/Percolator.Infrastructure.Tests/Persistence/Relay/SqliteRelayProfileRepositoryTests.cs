namespace Percolator.Infrastructure.Tests.Persistence.Relay;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteRelayProfileRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteRelayProfileRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteRelayProfileRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task CommitProfile_ValidatesWriteToken()
    {
        // Arrange
        var locator = BlindedProfileLocator.FromSpan(new byte[32]);
        var writeTokenBytes = new byte[32];
        Array.Fill(writeTokenBytes, (byte)0x99);
        var writeTokenHash = SHA256.HashData(writeTokenBytes);

        var package1 = new ProfileCiphertextPackage(new byte[12], new byte[1040]);
        var slot1 = new RelayHostedProfileSlot(
            locator,
            writeTokenHash,
            package1,
            revision: 1,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(7));

        // Act - Initial save
        var saveResult = await _repository.SaveSlotAsync(slot1);
        saveResult.IsSuccess.Should().BeTrue();

        // Attempt overwrite with wrong write token hash -> must fail
        var wrongHash = new byte[32];
        Array.Fill(wrongHash, (byte)0xEE);
        var badSlot = new RelayHostedProfileSlot(
            locator,
            wrongHash,
            package1,
            revision: 2,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(7));

        var badResult = await _repository.SaveSlotAsync(badSlot);
        badResult.IsSuccess.Should().BeFalse();
        badResult.Error.Code.Should().Be("UNAUTHORIZED_PROFILE_WRITE");

        // Attempt overwrite with correct token hash but stale revision -> must fail
        var staleSlot = new RelayHostedProfileSlot(
            locator,
            writeTokenHash,
            package1,
            revision: 1,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(7));

        var staleResult = await _repository.SaveSlotAsync(staleSlot);
        staleResult.IsSuccess.Should().BeFalse();
        staleResult.Error.Code.Should().Be("STALE_PROFILE_REVISION");

        // Overwrite with correct token and higher revision -> must succeed
        var updatedSlot = new RelayHostedProfileSlot(
            locator,
            writeTokenHash,
            package1,
            revision: 2,
            expiresAtUtc: DateTimeOffset.UtcNow.AddDays(14));

        var updateResult = await _repository.SaveSlotAsync(updatedSlot);
        updateResult.IsSuccess.Should().BeTrue();

        var retrieved = await _repository.GetSlotAsync(locator);
        retrieved.Should().NotBeNull();
        retrieved!.Revision.Should().Be(2);
    }
}
