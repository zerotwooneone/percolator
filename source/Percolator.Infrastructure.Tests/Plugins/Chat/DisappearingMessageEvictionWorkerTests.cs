namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.Infrastructure.Transport.Workers;
using Percolator.PluginSdk;

[TestFixture]
public sealed class DisappearingMessageEvictionWorkerTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset initial)
        {
            _utcNow = initial;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }

    private sealed class TestChunkStore : ILocalChunkStore
    {
        public HashSet<BlobId> DeletedBlobs { get; } = new();

        public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default)
        {
            DeletedBlobs.Add(blobId);
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task ExpiredMessages_PurgesRowsChannelPayloadsAndLocalCachedBlobs()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlChatMessageRepository(testDb.DbContext);

        var startTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startTime);
        var chunkStore = new TestChunkStore();
        var worker = new DisappearingMessageEvictionWorker(testDb.DbContext, timeProvider, chunkStore);

        ulong startMs = (ulong)startTime.ToUnixTimeMilliseconds();
        ulong expiryMs = startMs + 30_000UL; // 30 seconds

        var channelId = ChannelId.New();
        var authorId = PublicIdentityId.New();
        var expiredMsgId = PayloadId.New();
        var normalMsgId = PayloadId.New();

        var blobId = new BlobId("cafebabe12345678");
        var blobRef = new BlobReference
        {
            Id = blobId,
            CiphertextSha256 = new byte[32],
            EncryptionKey = new byte[32],
            BaseNonce = new byte[12],
            PlaintextSizeBytes = 500,
            CiphertextSizeBytes = 528,
            MimeType = "application/octet-stream",
            ExpiresAtUtc = startTime.AddSeconds(30),
            StorageMode = BlobStorageMode.PublicRelayBlob
        };

        var expiredMsg = new ChatMessage(
            expiredMsgId,
            channelId,
            authorId,
            startMs,
            "Disappearing soon",
            attachments: new[] { MediaAttachmentDto.FromOutOfBand(blobRef) },
            expireAfterSeconds: 30,
            expiresAtUnixMs: expiryMs);

        var normalMsg = new ChatMessage(
            normalMsgId,
            channelId,
            authorId,
            startMs,
            "Permanent message");

        await repo.AddAsync(expiredMsg);
        await repo.AddAsync(normalMsg);

        // Add corresponding ChatChannelPayloadEntity
        testDb.DbContext.ChatChannelPayloads.Add(new ChatChannelPayloadEntity
        {
            Id = expiredMsgId.Value,
            ChannelId = channelId.Value,
            AuthorId = authorId.Value,
            AuthorDeviceId = 1,
            EncryptedPayload = new byte[] { 1, 2, 3 },
            SentAtUtc = startTime
        });
        await testDb.DbContext.SaveChangesAsync();

        // 1. Advance virtual clock by 31s
        timeProvider.Advance(TimeSpan.FromSeconds(31));

        // 2. Execute sweep
        int purgedCount = await worker.SweepAsync();
        purgedCount.Should().Be(1);

        // 3. Assertions
        var remainingMessages = await testDb.DbContext.ChatMessages.ToListAsync();
        remainingMessages.Count.Should().Be(1);
        remainingMessages[0].Id.Should().Be(normalMsgId.Value);

        // Assert attachments purged
        var remainingAttachments = await testDb.DbContext.ChatAttachments.ToListAsync();
        remainingAttachments.Count.Should().Be(0);

        // Assert channel payloads purged
        var remainingPayloads = await testDb.DbContext.ChatChannelPayloads.ToListAsync();
        remainingPayloads.Count.Should().Be(0);

        // Assert local cached blob shredded via ILocalChunkStore
        chunkStore.DeletedBlobs.Should().Contain(blobId);
    }
}
