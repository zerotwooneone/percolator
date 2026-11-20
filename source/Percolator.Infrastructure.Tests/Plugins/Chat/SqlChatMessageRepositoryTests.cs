namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.PluginSdk;

[TestFixture]
public sealed class SqlChatMessageRepositoryTests
{
    [Test]
    public async Task SaveAndDeleteMessage_PersistsAttachmentsAndCascadesDeletion()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlChatMessageRepository(testDb.DbContext);

        var msgId = PayloadId.New();
        var channelId = ChannelId.New();
        var authorId = Domain.Identities.PublicIdentityId.New();

        byte[] rawKey = new byte[32];
        for (int i = 0; i < 32; i++) rawKey[i] = (byte)(i + 1);

        var blobRef = new BlobReference
        {
            Id = new BlobId("abcdef1234567890"),
            CiphertextSha256 = new byte[32],
            EncryptionKey = rawKey,
            BaseNonce = new byte[12],
            PlaintextSizeBytes = 1024,
            CiphertextSizeBytes = 1040,
            MimeType = "image/png",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            StorageMode = BlobStorageMode.PublicRelayBlob,
            RelayEndpointUri = "https://relay.test.percolator/blob"
        };

        var attachment = MediaAttachmentDto.FromOutOfBand(blobRef, "test.png");
        var mention = new MentionDto(authorId, 0, 5);

        var message = new ChatMessage(
            msgId,
            channelId,
            authorId,
            1700000000000UL,
            "Check this attachment",
            mentions: new[] { mention },
            attachments: new[] { attachment });

        await repo.AddAsync(message);

        // 1. Verify that relational row exists in chat_attachments with correct raw key (protected at rest by SQLCipher)
        using (var cmd = testDb.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT encryption_key, blob_id FROM chat_attachments WHERE message_id = @mid;";
            cmd.Parameters.Add(new SqliteParameter("@mid", msgId.Value));

            using var reader = await cmd.ExecuteReaderAsync();
            reader.Read().Should().BeTrue();
            byte[] storedKey = (byte[])reader[0];
            string storedBlobId = (string)reader[1];
            storedKey.Should().Equal(rawKey, "Key is stored in relational column and protected by SQLCipher page encryption");
            storedBlobId.Should().Be("abcdef1234567890");
        }

        // 2. Verify that mentions exist in chat_message_mentions
        using (var cmd = testDb.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chat_message_mentions WHERE message_id = @mid;";
            cmd.Parameters.Add(new SqliteParameter("@mid", msgId.Value));
            long count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(1);
        }

        // 3. Verify reading through repository rehydrates correctly
        var loaded = await repo.GetByIdAsync(msgId);
        loaded.Should().NotBeNull();
        loaded!.Attachments.Count.Should().Be(1);
        loaded.Attachments[0].OutOfBandReference!.EncryptionKey.Should().Equal(rawKey);
        loaded.Mentions.Count.Should().Be(1);

        // 4. Verify cascade deletion
        var entity = await testDb.DbContext.ChatMessages.FindAsync(msgId.Value);
        if (entity != null)
        {
            testDb.DbContext.ChatMessages.Remove(entity);
            await testDb.DbContext.SaveChangesAsync();
        }

        using (var cmd = testDb.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chat_attachments WHERE message_id = @mid;";
            cmd.Parameters.Add(new SqliteParameter("@mid", msgId.Value));
            long count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(0, "chat_attachments must be cascade deleted when message is deleted");
        }

        using (var cmd = testDb.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chat_message_mentions WHERE message_id = @mid;";
            cmd.Parameters.Add(new SqliteParameter("@mid", msgId.Value));
            long count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(0, "chat_message_mentions must be cascade deleted when message is deleted");
        }
    }

    [Test]
    public async Task AddAsync_And_UpdateAsync_PersistAndRetrieve_DeliveredAtUnixMs()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlChatMessageRepository(testDb.DbContext);

        var msgId = PayloadId.New();
        var channelId = ChannelId.New();
        var authorId = Domain.Identities.PublicIdentityId.New();

        // 1. Add message initially without delivery or read timestamps
        var message = new ChatMessage(
            msgId,
            channelId,
            authorId,
            1700000000000UL,
            "Testing delivery receipt persistence",
            deliveredAtUnixMs: null,
            readAtUnixMs: null);

        await repo.AddAsync(message);

        var loadedInitial = await repo.GetByIdAsync(msgId);
        loadedInitial.Should().NotBeNull();
        loadedInitial!.DeliveredAtUnixMs.Should().BeNull();
        loadedInitial.ReadAtUnixMs.Should().BeNull();

        // 2. Mark as delivered and update
        const ulong deliveredAt = 1700000005000UL;
        message.MarkAsDelivered(deliveredAt);
        await repo.UpdateAsync(message);

        var loadedAfterDelivery = await repo.GetByIdAsync(msgId);
        loadedAfterDelivery.Should().NotBeNull();
        loadedAfterDelivery!.DeliveredAtUnixMs.Should().Be(deliveredAt);
        loadedAfterDelivery.ReadAtUnixMs.Should().BeNull();

        // Verify direct SQLite column value via ADO.NET
        using (var cmd = testDb.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT delivered_at_unix_ms, read_at_unix_ms FROM chat_messages WHERE id = @mid;";
            cmd.Parameters.Add(new SqliteParameter("@mid", msgId.Value));

            using var reader = await cmd.ExecuteReaderAsync();
            reader.Read().Should().BeTrue();
            Convert.ToInt64(reader["delivered_at_unix_ms"]).Should().Be((long)deliveredAt);
            reader.IsDBNull(reader.GetOrdinal("read_at_unix_ms")).Should().BeTrue();
        }

        // 3. Mark as read and update
        const ulong readAt = 1700000010000UL;
        message.MarkAsRead(readAt);
        await repo.UpdateAsync(message);

        var loadedAfterRead = await repo.GetByIdAsync(msgId);
        loadedAfterRead.Should().NotBeNull();
        loadedAfterRead!.DeliveredAtUnixMs.Should().Be(deliveredAt);
        loadedAfterRead.ReadAtUnixMs.Should().Be(readAt);

        // 4. Also verify AddAsync with timestamps already populated
        var msgId2 = PayloadId.New();
        const ulong deliveredAt2 = 1700000020000UL;
        const ulong readAt2 = 1700000025000UL;
        var message2 = new ChatMessage(
            msgId2,
            channelId,
            authorId,
            1700000015000UL,
            "Message with pre-existing delivery timestamps",
            deliveredAtUnixMs: deliveredAt2,
            readAtUnixMs: readAt2);

        await repo.AddAsync(message2);

        var loaded2 = await repo.GetByIdAsync(msgId2);
        loaded2.Should().NotBeNull();
        loaded2!.DeliveredAtUnixMs.Should().Be(deliveredAt2);
        loaded2.ReadAtUnixMs.Should().Be(readAt2);
    }
}
