namespace Percolator.Infrastructure.Transport.Workers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Apps.Chat.Features.GroupInvitations;

public sealed class AbandonedInvitePruner : BackgroundService
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IGroupInvitationRepository? _directRepository;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;

    [ActivatorUtilitiesConstructor]
    public AbandonedInvitePruner(
        IServiceScopeFactory scopeFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromHours(1);
    }

    public AbandonedInvitePruner(
        IGroupInvitationRepository repository,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _directRepository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? TimeSpan.FromHours(1);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        ulong nowMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (_directRepository != null)
        {
            return await _directRepository.PurgeExpiredAsync(nowMs, ct);
        }

        using var scope = _scopeFactory!.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGroupInvitationRepository>();
        return await repo.PurgeExpiredAsync(nowMs, ct);
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
