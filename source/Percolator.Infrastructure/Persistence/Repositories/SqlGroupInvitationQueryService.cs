namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

public sealed class SqlGroupInvitationQueryService : IGroupInvitationQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SqlGroupInvitationQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(CancellationToken ct = default)
    {
        var entities = await _dbContext.ChatGroupInvitations
            .AsNoTracking()
            .Where(i => i.Status == (int)GroupInvitationStatus.Pending)
            .OrderBy(i => i.ExpiresAtUnixMs)
            .ToListAsync(ct);

        return entities.Select(e => new GroupInvitationSummaryReadModel(
            new InvitationId(e.Id),
            ChannelId.FromGuid(e.TargetGroupChannelId),
            e.GroupName,
            PublicIdentityId.FromGuid(new Guid(e.InviterId, bigEndian: true)),
            e.ExpiresAtUnixMs,
            (GroupInvitationStatus)e.Status)).ToList();
    }
}
