namespace Percolator.Infrastructure.Tests.Persistence;

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

[TestFixture]
public sealed class SqlitePendingIngressStoreTests
{
    private SqliteTestDb _testDb = null!;
    private SqlitePendingIngressStore _store = null!;
    private PublicIdentityId _owner;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _store = new SqlitePendingIngressStore(_testDb.DbContext);
        _owner = PublicIdentityId.New();
    }

    [TearDown]
    public void TearDown() => _testDb.Dispose();

    private static QueuedMailboxEnvelope Queued(string queueId, byte ciphertextByte = 0xAA) =>
        new(
            MailboxQueueId.Parse(queueId),
            new MailboxEnvelope(
                EnvelopeId.New(),
                BlindedRoutingToken.New(),
                new byte[] { ciphertextByte, 0x0B },
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1)));

    private async Task<OpsPendingIngressEnvelopeEntity?> FindRowAsync(
        PublicIdentityId owner, string queueId, PercolatorDbContext db) =>
        await db.OpsPendingIngressEnvelopes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.OwnerIdentityId == owner.Value && e.QueueId == queueId);

    [Test]
    public async Task Upsert_NewEnvelope_PersistsCiphertextSpoolRow()
    {
        var queued = Queued("q-1", ciphertextByte: 0x5A);

        var record = await _store.UpsertAsync(_owner, queued);

        record.QueueId.Should().Be(queued.QueueId);
        record.Attempts.Should().Be(0);
        record.LastError.Should().BeNull();
        record.Queued.Envelope.Ciphertext.ToArray().Should().Equal(0x5A, 0x0B);
        record.Queued.Envelope.RecipientToken.Should().Be(queued.Envelope.RecipientToken);

        using var db = _testDb.CreateNewContext();
        var row = await FindRowAsync(_owner, "q-1", db);
        row.Should().NotBeNull();
        row!.SequenceNumber.Should().BeGreaterThan(0);
        row.Ciphertext.Should().Equal(0x5A, 0x0B);
        row.RoutingToken.Should().Equal(queued.Envelope.RecipientToken.ToByteArray());
    }

    [Test]
    public async Task Upsert_DuplicateQueueId_ReturnsExistingRow_WithoutResettingAttempts()
    {
        var queued = Queued("q-1");
        await _store.UpsertAsync(_owner, queued);
        await _store.RecordTransientFailureAsync(_owner, queued.QueueId, new DomainError("E1", "first"));

        var again = await _store.UpsertAsync(_owner, Queued("q-1", ciphertextByte: 0x77));

        again.Attempts.Should().Be(1);
        again.LastError!.Value.Code.Should().Be("E1");
        again.Queued.Envelope.Ciphertext.ToArray().Should().Equal(0xAA, 0x0B); // original ciphertext kept

        using var db = _testDb.CreateNewContext();
        (await db.OpsPendingIngressEnvelopes.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task ListByOwner_ReturnsRecordsInReceiveOrder()
    {
        await _store.UpsertAsync(_owner, Queued("q-3"));
        await _store.UpsertAsync(_owner, Queued("q-1"));
        await _store.UpsertAsync(_owner, Queued("q-2"));

        var records = await _store.ListByOwnerAsync(_owner);

        records.Select(r => r.QueueId.Value).Should().Equal("q-3", "q-1", "q-2");
    }

    [Test]
    public async Task ListByOwner_FiltersToOwner()
    {
        var other = PublicIdentityId.New();
        await _store.UpsertAsync(_owner, Queued("q-1"));
        await _store.UpsertAsync(other, Queued("q-2"));

        var records = await _store.ListByOwnerAsync(_owner);

        records.Select(r => r.QueueId.Value).Should().Equal("q-1");
    }

    [Test]
    public async Task ListByOwner_RehydratesTimestampsAndError()
    {
        var queued = Queued("q-1");
        await _store.UpsertAsync(_owner, queued);
        await _store.RecordTransientFailureAsync(_owner, queued.QueueId, new DomainError("DB_BUSY", "locked"));

        var record = (await _store.ListByOwnerAsync(_owner)).Single();

        record.Attempts.Should().Be(1);
        record.LastError!.Value.Code.Should().Be("DB_BUSY");
        record.LastError!.Value.Description.Should().Be("locked");
        record.Queued.Envelope.EnqueuedAtUtc.Should().Be(queued.Envelope.EnqueuedAtUtc);
        record.Queued.Envelope.ExpiresAtUtc.Should().Be(queued.Envelope.ExpiresAtUtc);
    }

    [Test]
    public async Task RecordTransientFailure_IncrementsAttemptsPerCall()
    {
        var queued = Queued("q-1");
        await _store.UpsertAsync(_owner, queued);

        await _store.RecordTransientFailureAsync(_owner, queued.QueueId, new DomainError("A", "a"));
        await _store.RecordTransientFailureAsync(_owner, queued.QueueId, new DomainError("B", "b"));

        using var db = _testDb.CreateNewContext();
        var row = await FindRowAsync(_owner, "q-1", db);
        row!.Attempts.Should().Be(2);
        row.LastErrorCode.Should().Be("B");
        row.LastErrorDescription.Should().Be("b");
    }

    [Test]
    public async Task RecordTransientFailure_MissingRow_NoOps()
    {
        var act = async () => await _store.RecordTransientFailureAsync(
            _owner, MailboxQueueId.Parse("ghost"), new DomainError("E", "x"));

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Delete_RemovesRow_AndZeroizesCiphertextBeforeRemoval()
    {
        var queued = Queued("q-1", ciphertextByte: 0xCC);
        // Track the entity on the same ambient context so the store's load returns this
        // same instance — lets us observe the zeroization that runs before removal.
        var entity = new OpsPendingIngressEnvelopeEntity
        {
            OwnerIdentityId = _owner.Value,
            QueueId = queued.QueueId.Value,
            RoutingToken = queued.Envelope.RecipientToken.ToByteArray(),
            Ciphertext = queued.Envelope.Ciphertext.ToArray(),
            EnqueuedAtUtc = queued.Envelope.EnqueuedAtUtc,
            ExpiresAtUtc = queued.Envelope.ExpiresAtUtc,
        };
        _testDb.DbContext.OpsPendingIngressEnvelopes.Add(entity);
        await _testDb.DbContext.SaveChangesAsync();

        await _store.DeleteAsync(_owner, queued.QueueId);

        entity.Ciphertext.Should().OnlyContain(b => b == 0);
        using var db = _testDb.CreateNewContext();
        (await FindRowAsync(_owner, "q-1", db)).Should().BeNull();
    }

    [Test]
    public async Task Delete_MissingRow_NoOps()
    {
        var act = async () => await _store.DeleteAsync(_owner, MailboxQueueId.Parse("ghost"));

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task StoreMethods_WithInvalidOwnerId_ThrowArgumentException()
    {
        var queued = Queued("q-1");
        var invalidOwner = default(PublicIdentityId);

        await FluentActions.Invoking(() => _store.UpsertAsync(invalidOwner, queued))
            .Should().ThrowAsync<ArgumentException>();

        await FluentActions.Invoking(() => _store.ListByOwnerAsync(invalidOwner))
            .Should().ThrowAsync<ArgumentException>();

        await FluentActions.Invoking(() => _store.DeleteAsync(invalidOwner, queued.QueueId))
            .Should().ThrowAsync<ArgumentException>();

        await FluentActions.Invoking(() => _store.RecordTransientFailureAsync(invalidOwner, queued.QueueId, new DomainError("E", "x")))
            .Should().ThrowAsync<ArgumentException>();
    }
}
