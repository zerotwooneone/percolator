namespace Percolator.Infrastructure.Transport.Workers;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application2.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.PluginSdk;

public sealed class DisappearingMessageEvictionWorker : BackgroundService
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly PercolatorDbContext? _directDbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalChunkStore? _chunkStore;
    private readonly TimeSpan _interval;

    public DisappearingMessageEvictionWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider? timeProvider = null,
        ILocalChunkStore? chunkStore = null,
        TimeSpan? interval = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chunkStore = chunkStore;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public DisappearingMessageEvictionWorker(
        PercolatorDbContext dbContext,
        TimeProvider? timeProvider = null,
        ILocalChunkStore? chunkStore = null,
        TimeSpan? interval = null)
    {
        _directDbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chunkStore = chunkStore;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (_directDbContext != null)
        {
            return await ExecuteSweepOnDbContextAsync(_directDbContext, _chunkStore, ct);
        }

        using var scope = _scopeFactory!.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();
        var chunkStore = scope.ServiceProvider.GetService<ILocalChunkStore>() ?? _chunkStore;

        return await ExecuteSweepOnDbContextAsync(dbContext, chunkStore, ct);
    }

    private async Task<int> ExecuteSweepOnDbContextAsync(
        PercolatorDbContext dbContext,
        ILocalChunkStore? chunkStore,
        CancellationToken ct)
    {
        ulong nowMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var expiredMessages = await dbContext.ChatMessages
            .Where(m => m.ExpiresAtUnixMs.HasValue && m.ExpiresAtUnixMs.Value <= nowMs)
            .ToListAsync(ct);

        if (expiredMessages.Count == 0)
        {
            return 0;
        }

        var expiredIds = expiredMessages.Select(m => m.Id).ToList();

        // Query chat_attachments to shred locally cached media files prior to cascade deletion
        var attachments = await dbContext.ChatAttachments
            .Where(a => expiredIds.Contains(a.MessageId))
            .ToListAsync(ct);

        if (chunkStore != null && attachments.Count > 0)
        {
            foreach (var att in attachments)
            {
                if (!string.IsNullOrEmpty(att.BlobId))
                {
                    await chunkStore.DeleteBlobAsync(new BlobId(att.BlobId), ct);
                }
                else if (att.BlobSha256 is { Length: > 0 })
                {
                    await chunkStore.DeleteBlobAsync(BlobId.FromSha256(att.BlobSha256), ct);
                }
            }
        }

        // Purge corresponding chat_channel_payloads rows so ciphertext payloads are shredded
        var channelPayloads = await dbContext.ChatChannelPayloads
            .Where(p => expiredIds.Contains(p.Id))
            .ToListAsync(ct);

        if (channelPayloads.Count > 0)
        {
            dbContext.ChatChannelPayloads.RemoveRange(channelPayloads);
        }

        // Purge expired messages (cascades to chat_attachments and chat_reactions)
        dbContext.ChatMessages.RemoveRange(expiredMessages);

        await dbContext.SaveChangesAsync(ct);
        return expiredMessages.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
                await Task.Delay(_interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
