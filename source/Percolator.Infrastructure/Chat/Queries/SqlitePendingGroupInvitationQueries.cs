using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;
using System.Text.Json;

namespace Percolator.Infrastructure.Chat.Queries;

/// <summary>
/// SQLite-backed implementation of IPendingGroupInvitationQueries.
/// </summary>
public sealed class SqlitePendingGroupInvitationQueries : IPendingGroupInvitationQueries
{
    private readonly PercolatorDbContext _db;

    public SqlitePendingGroupInvitationQueries(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<List<PendingGroupInvitationDto>> GetPendingInvitationsAsync(CancellationToken cancellationToken = default)
    {
        var pendingInvitations = await _db.PendingGroupInvitations
            .AsNoTracking()
            .Where(i => i.Status == PendingGroupInvitationStatus.Pending)
            .ToListAsync(cancellationToken);

        var dtos = new List<PendingGroupInvitationDto>();

        foreach (var invitation in pendingInvitations)
        {
            // Deserialize initial members from JSON
            List<byte[]> initialMembers;
            try
            {
                initialMembers = JsonSerializer.Deserialize<List<byte[]>>(invitation.InitialMembersJson) ?? new List<byte[]>();
            }
            catch (JsonException)
            {
                initialMembers = new List<byte[]>();
            }

            dtos.Add(new PendingGroupInvitationDto
            {
                ConversationId = new Percolator.Chat.Messaging.ValueObjects.ConversationId(invitation.ConversationId),
                InviterPeerId = new Percolator.Chat.GroupMembership.ChatPeerId(invitation.InviterPeerId),
                CreatorIdentityKey = invitation.CreatorIdentityKey,
                InitialMembers = initialMembers,
                GroupName = invitation.GroupName,
                ReceivedAtUtc = invitation.ReceivedAtUtc
            });
        }

        return dtos;
    }
}
