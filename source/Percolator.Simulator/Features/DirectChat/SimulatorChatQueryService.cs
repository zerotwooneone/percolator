using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed class SimulatorChatQueryService : ISimulatorChatQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SimulatorChatQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<SimulatorChatMessageReadModel>> GetTranscriptAsync(
        ChannelId channelId,
        CancellationToken ct = default)
    {
        var channelGuid = channelId.Value;

        var messages = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => m.ChannelId == channelGuid && !m.IsTombstone)
            .OrderBy(m => m.TimestampUnixMs)
            .Select(m => new
            {
                m.Id,
                m.ChannelId,
                m.AuthorId,
                m.Content,
                m.TimestampUnixMs,
                m.DeliveredAtUnixMs,
                m.ReadAtUnixMs,
                m.EditedAtUnixMs
            })
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            return Array.Empty<SimulatorChatMessageReadModel>();
        }

        var messageIds = messages.Select(m => m.Id).ToList();
        var reactions = await _dbContext.ChatReactions
            .AsNoTracking()
            .Where(r => messageIds.Contains(r.MessageId))
            .ToListAsync(ct);

        var reactionsByMessage = reactions
            .GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Emoji).ToList());

        var result = new List<SimulatorChatMessageReadModel>(messages.Count);
        foreach (var m in messages)
        {
            reactionsByMessage.TryGetValue(m.Id, out var msgReactions);
            result.Add(new SimulatorChatMessageReadModel(
                m.Id,
                m.ChannelId,
                PublicIdentityId.FromBytes(m.AuthorId),
                m.Content,
                m.TimestampUnixMs,
                m.DeliveredAtUnixMs,
                m.ReadAtUnixMs,
                m.EditedAtUnixMs,
                msgReactions ?? (IReadOnlyList<string>)Array.Empty<string>()));
        }

        return result;
    }

    public async Task<bool> HasMessageContainingAsync(
        string expectedText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedText)) return false;

        return await _dbContext.ChatMessages
            .AsNoTracking()
            .AnyAsync(m => !m.IsTombstone && m.Content.Contains(expectedText), ct);
    }

    public async Task<SimulatorChatMessageReadModel?> FindMessageAsync(
        string expectedText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedText)) return null;

        var entity = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => !m.IsTombstone && m.Content.Contains(expectedText))
            .OrderByDescending(m => m.TimestampUnixMs)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
        {
            entity = await _dbContext.ChatMessages
                .AsNoTracking()
                .Where(m => m.Content.Contains(expectedText))
                .OrderByDescending(m => m.TimestampUnixMs)
                .FirstOrDefaultAsync(ct);

            if (entity == null) return null;
        }

        var reactions = await _dbContext.ChatReactions
            .AsNoTracking()
            .Where(r => r.MessageId == entity.Id)
            .Select(r => r.Emoji)
            .ToListAsync(ct);

        return new SimulatorChatMessageReadModel(
            entity.Id,
            entity.ChannelId,
            PublicIdentityId.FromBytes(entity.AuthorId),
            entity.Content,
            entity.TimestampUnixMs,
            entity.DeliveredAtUnixMs,
            entity.ReadAtUnixMs,
            entity.EditedAtUnixMs,
            reactions);
    }

    public async Task<bool> HasReactionAsync(
        string expectedText,
        string expectedEmoji,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedText) || string.IsNullOrEmpty(expectedEmoji)) return false;

        var matchingMessageIds = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => !m.IsTombstone && m.Content.Contains(expectedText))
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (matchingMessageIds.Count == 0) return false;

        return await _dbContext.ChatReactions
            .AsNoTracking()
            .AnyAsync(r => matchingMessageIds.Contains(r.MessageId) && r.Emoji == expectedEmoji, ct);
    }

    public async Task<bool> IsMessageEditedAsync(
        string expectedText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedText)) return false;

        return await _dbContext.ChatMessages
            .AsNoTracking()
            .AnyAsync(m => !m.IsTombstone && m.Content.Contains(expectedText) && m.EditedAtUnixMs.HasValue, ct);
    }

    public async Task<bool> HasAnyTombstoneAsync(
        CancellationToken ct = default)
    {
        return await _dbContext.ChatMessages
            .AsNoTracking()
            .AnyAsync(m => m.IsTombstone && m.Content.Contains("deleted"), ct);
    }

    public async Task<bool> IsMessageReadAsync(
        string expectedText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedText)) return false;

        return await _dbContext.ChatMessages
            .AsNoTracking()
            .AnyAsync(m => !m.IsTombstone && m.Content.Contains(expectedText) && m.ReadAtUnixMs.HasValue, ct);
    }
}
