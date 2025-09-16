using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Percolator.Application.Apps.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat
{
    public sealed class SqliteActingAdminResolver : IActingAdminResolver
    {
        private readonly ILogger<SqliteActingAdminResolver> _logger;
        private readonly PercolatorDbContext _db;
        public SqliteActingAdminResolver(ILogger<SqliteActingAdminResolver> logger, PercolatorDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        public async Task<Guid?> GetActingAdminPeerIdAsync(Guid conversationId, CancellationToken ct)
        {
            var row = await _db.GroupAdminOps.AsNoTracking()
                .Where(x => x.ConversationId == conversationId && x.ActingAdminPeerId != null)
                .OrderByDescending(x => x.AppliedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (row?.ActingAdminPeerId is Guid id)
            {
                return id;
            }
            _logger.LogDebug("[SqliteActingAdminResolver] No acting admin recorded yet for conversation {ConversationId}", conversationId);
            return null;
        }
    }
}
