namespace Percolator.Infrastructure.Tests.Plugins.Chat;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlConversationListQueryServiceTests
{
    [Test]
    public async Task UnreadBadgeCount_AggregatesPastReadReceipt()
    {
        using var testDb = new SqliteTestDb();
        var repo = new SqlChatMessageRepository(testDb.DbContext);
        var convQueryService = new SqlConversationListQueryService(testDb.ContextFactory);

        var channelId = ChannelId.New();
        var currentUserId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();

        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = channelId.Value,
            Type = 0,
            Name = "Alice & Bob",
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peerId.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });
        await testDb.DbContext.SaveChangesAsync();

        // Msg 1 from peer: read
        var msg1 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1000UL, "Msg 1",
            readAtUnixMs: 1050UL);
        await repo.AddAsync(msg1);

        // Msg 2 from peer: read
        var msg2 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1010UL, "Msg 2",
            readAtUnixMs: 1050UL);
        await repo.AddAsync(msg2);

        // Msg 3 from peer: unread (past read receipt)
        var msg3 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1100UL, "Msg 3 unread",
            readAtUnixMs: null);
        await repo.AddAsync(msg3);

        // Msg 4 from current user: unread by peer, but current user sent it, so shouldn't count towards current user's unread badge!
        var msg4 = new ChatMessage(
            PayloadId.New(), channelId, currentUserId, 1150UL, "Msg 4 from me",
            readAtUnixMs: null);
        await repo.AddAsync(msg4);

        // Msg 5 from peer: unread (past read receipt)
        var msg5 = new ChatMessage(
            PayloadId.New(), channelId, peerId, 1200UL, "Msg 5 latest unread",
            readAtUnixMs: null);
        await repo.AddAsync(msg5);

        var summaries = await convQueryService.GetConversationsAsync(
            currentUserId,
            currentTimestampMs: 2000UL);

        summaries.Count.Should().Be(1);
        var summary = summaries[0];
        summary.ChannelId.Should().Be(channelId);
        summary.Title.Should().Be("Alice & Bob");
        summary.LastMessageSnippet.Should().Be("Msg 5 latest unread");
        summary.LastActivityTimestampUnixMs.Should().Be(1200UL);
        summary.LastAuthorId.Should().Be(peerId);
        summary.UnreadCount.Should().Be(2, "Only peer's unread messages past read receipt should count");
    }

    [Test]
    public async Task ReturnsZeroMessageChannels_WithEmptySnippetAndActivityTimestamp()
    {
        using var testDb = new SqliteTestDb();
        var convQueryService = new SqlConversationListQueryService(testDb.ContextFactory);

        var channelId = ChannelId.New();
        var currentUserId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var createdUtc = DateTimeOffset.UtcNow.AddMinutes(-10);

        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = channelId.Value,
            Type = 0,
            Name = string.Empty,
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peerId.Value,
            Status = (int)DirectChannelStatus.PendingAcceptance,
            CreatedAtUtc = createdUtc,
            LastActivityUtc = createdUtc
        });
        await testDb.DbContext.SaveChangesAsync();

        var summaries = await convQueryService.GetConversationsAsync(
            currentUserId,
            currentTimestampMs: 5000UL);

        summaries.Count.Should().Be(1);
        var summary = summaries[0];
        summary.ChannelId.Should().Be(channelId);
        summary.Kind.Should().Be(ConversationChannelKind.Direct);
        summary.RemotePeerId.Should().Be(peerId);
        summary.LastMessageSnippet.Should().Be(string.Empty);
        summary.LastActivityTimestampUnixMs.Should().Be((ulong)createdUtc.ToUnixTimeMilliseconds());
        summary.UnreadCount.Should().Be(0);
        summary.LastAuthorId.Should().BeNull();
        summary.IsPendingAcceptance.Should().BeTrue();
    }

    [Test]
    public async Task ResolvesTitleHierarchy_ContactNicknameOverridesDisplayNameAndPeerId()
    {
        using var testDb = new SqliteTestDb();
        var convQueryService = new SqlConversationListQueryService(testDb.ContextFactory);

        var currentUserId = Domain.Identities.PublicIdentityId.New();
        var peer1 = Domain.Identities.PublicIdentityId.New();
        var peer2 = Domain.Identities.PublicIdentityId.New();
        var peer3 = Domain.Identities.PublicIdentityId.New();

        // Peer 1: has Contact Nickname
        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = Guid.NewGuid(),
            Type = 0,
            Name = string.Empty,
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peer1.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });
        testDb.DbContext.PeerContacts.Add(new PeerContactEntity
        {
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peer1.Value,
            Nickname = "Alice Nickname",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        // Peer 2: has Profile DisplayName only
        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = Guid.NewGuid(),
            Type = 0,
            Name = string.Empty,
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peer2.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });
        testDb.DbContext.PeerProfiles.Add(new PeerProfileEntity
        {
            PeerId = peer2.Value,
            DisplayName = "Bob Profile",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        // Peer 3: has neither -> falls back to truncated peer ID
        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = Guid.NewGuid(),
            Type = 0,
            Name = string.Empty,
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peer3.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });

        await testDb.DbContext.SaveChangesAsync();

        var summaries = await convQueryService.GetConversationsAsync(
            currentUserId,
            currentTimestampMs: 5000UL);

        summaries.Count.Should().Be(3);
        var s1 = summaries.First(s => s.RemotePeerId == peer1);
        s1.Title.Should().Be("Alice Nickname");

        var s2 = summaries.First(s => s.RemotePeerId == peer2);
        s2.Title.Should().Be("Bob Profile");

        var s3 = summaries.First(s => s.RemotePeerId == peer3);
        s3.Title.Should().Be($"Peer {peer3.Value:N}"[..12]);
    }

    [Test]
    public async Task ResolvesRelayIdentityId_WhenRelayEndpointMatches()
    {
        using var testDb = new SqliteTestDb();
        var convQueryService = new SqlConversationListQueryService(testDb.ContextFactory);

        var currentUserId = Domain.Identities.PublicIdentityId.New();
        var peerId = Domain.Identities.PublicIdentityId.New();
        var relayId = Domain.Identities.PublicIdentityId.New();
        const string relayEndpoint = "percolator://relay.example.com:5100";

        testDb.DbContext.RelayRegistrations.Add(new RelayRegistrationEntity
        {
            RelayIdentityId = relayId.Value,
            Endpoint = relayEndpoint,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = Guid.NewGuid(),
            Type = 0,
            Name = string.Empty,
            OwnerIdentityId = currentUserId.Value,
            RemotePeerId = peerId.Value,
            RelayEndpoint = relayEndpoint,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });

        await testDb.DbContext.SaveChangesAsync();

        var summaries = await convQueryService.GetConversationsAsync(
            currentUserId,
            currentTimestampMs: 5000UL);

        summaries.Count.Should().Be(1);
        summaries[0].RelayEndpoint.Should().Be(relayEndpoint);
        summaries[0].RelayIdentityId.Should().Be(relayId);
    }

    [Test]
    public async Task GroupChannel_ReturnsCorrectKindAndTitleAndNullRemotePeerId()
    {
        using var testDb = new SqliteTestDb();
        var convQueryService = new SqlConversationListQueryService(testDb.ContextFactory);

        var currentUserId = Domain.Identities.PublicIdentityId.New();
        var groupChannelId = Guid.NewGuid();

        testDb.DbContext.ChatChannels.Add(new ChatChannelEntity
        {
            Id = groupChannelId,
            Type = 1,
            Name = "Engineering Team",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastActivityUtc = DateTimeOffset.UtcNow
        });

        testDb.DbContext.ChatChannelMembers.Add(new ChatChannelMemberEntity
        {
            ChannelId = groupChannelId,
            MemberIdentityId = currentUserId.Value,
            Role = 0,
            JoinedAtUtc = DateTimeOffset.UtcNow
        });

        await testDb.DbContext.SaveChangesAsync();

        var summaries = await convQueryService.GetConversationsAsync(
            currentUserId,
            currentTimestampMs: 5000UL);

        summaries.Count.Should().Be(1);
        var groupSummary = summaries[0];
        groupSummary.Kind.Should().Be(ConversationChannelKind.Group);
        groupSummary.Title.Should().Be("Engineering Team");
        groupSummary.RemotePeerId.Should().BeNull();
        groupSummary.IsPendingAcceptance.Should().BeFalse();
    }
}
