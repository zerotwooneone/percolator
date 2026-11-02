namespace Percolator.Infrastructure.Tests.Maintenance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Infrastructure.Maintenance;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

[TestFixture]
public sealed class PrunerLockSafetyTests
{
    private sealed class FileBasedDbContextFactory : IDbContextFactory<PercolatorDbContext>
    {
        private readonly string _connectionString;

        public FileBasedDbContextFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public PercolatorDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PercolatorDbContext>()
                .UseSqlite(_connectionString)
                .Options;

            return new PercolatorDbContext(options);
        }

        public Task<PercolatorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    [Test]
    public async Task RetentionPruning_UsesBatchingUnderConcurrency()
    {
        SQLitePCL.Batteries_V2.Init();
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"perc_pruner_lock_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={tempDbPath};Pooling=false;";

        var factory = new FileBasedDbContextFactory(connectionString);

        await using (var initContext = factory.CreateDbContext())
        {
            await initContext.Database.EnsureCreatedAsync();
            await initContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;");
        }

        try
        {
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var timeProvider = new TestTimeProvider(t0);
            var pruner = new OutboxRetentionPruner(factory, timeProvider);

            const int deliveredCount = 1200;
            const int pendingCount = 50;

            await using (var seedContext = factory.CreateDbContext())
            {
                var deliveredJobs = new List<OpsOutboxJobEntity>(deliveredCount);
                for (int i = 0; i < deliveredCount; i++)
                {
                    deliveredJobs.Add(new OpsOutboxJobEntity
                    {
                        Id = Guid.NewGuid(),
                        OwnerIdentityId = Guid.NewGuid(),
                        ChannelId = Guid.NewGuid(),
                        Status = (int)OutboxStatus.Delivered,
                        CreatedAtUtc = t0.AddMinutes(i),
                        Payload = Array.Empty<byte>()
                    });
                }

                var pendingJobs = new List<OpsOutboxJobEntity>(pendingCount);
                for (int i = 0; i < pendingCount; i++)
                {
                    pendingJobs.Add(new OpsOutboxJobEntity
                    {
                        Id = Guid.NewGuid(),
                        OwnerIdentityId = Guid.NewGuid(),
                        ChannelId = Guid.NewGuid(),
                        Status = (int)OutboxStatus.Pending,
                        CreatedAtUtc = t0.AddMinutes(i),
                        Payload = new byte[] { 1, 2, 3 }
                    });
                }

                seedContext.OpsOutboxJobs.AddRange(deliveredJobs);
                seedContext.OpsOutboxJobs.AddRange(pendingJobs);
                await seedContext.SaveChangesAsync();
            }

            // Advance clock past 7 days (cutoff is t0 + 1 day)
            timeProvider.Advance(TimeSpan.FromDays(8));

            using var cts = new CancellationTokenSource();
            int successfulReads = 0;

            var readerTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await using var readContext = await factory.CreateDbContextAsync(cts.Token);
                        var active = await readContext.OpsOutboxJobs
                            .Where(j => j.Status == (int)OutboxStatus.Pending)
                            .CountAsync(cts.Token);

                        if (active == pendingCount)
                        {
                            Interlocked.Increment(ref successfulReads);
                        }

                        await Task.Yield();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            int totalPurged = await pruner.SweepAsync();

            await cts.CancelAsync();
            try
            {
                await readerTask;
            }
            catch (OperationCanceledException)
            {
            }

            totalPurged.Should().Be(deliveredCount);
            successfulReads.Should().BeGreaterThan(0);

            await using (var verifyContext = factory.CreateDbContext())
            {
                var remaining = await verifyContext.OpsOutboxJobs.ToListAsync();
                remaining.Should().HaveCount(pendingCount);
                remaining.All(j => j.Status == (int)OutboxStatus.Pending).Should().BeTrue();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(tempDbPath);
            TryDeleteFile(tempDbPath + "-wal");
            TryDeleteFile(tempDbPath + "-shm");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
