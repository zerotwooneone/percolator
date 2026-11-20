using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorRelayQueryService : ISimulatorRelayQueryService
{
    private readonly IDbContextFactory<PercolatorDbContext>? _contextFactory;
    private readonly PercolatorDbContext? _dbContext;

    [ActivatorUtilitiesConstructor]
    public SimulatorRelayQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public SimulatorRelayQueryService(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    private async Task<TResult> WithContextAsync<TResult>(Func<PercolatorDbContext, Task<TResult>> action)
    {
        if (_contextFactory != null)
        {
            await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            return await action(context).ConfigureAwait(false);
        }

        return await action(_dbContext!).ConfigureAwait(false);
    }

    public async Task<int> GetHostedOpkCountAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        var opks = await GetHostedOneTimePreKeysAsync(routingToken, deviceId, ct);
        return opks.Count;
    }

    public Task<bool> HasActivePreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        var routingBytes = routingToken.ToByteArray();
        return WithContextAsync(async dbContext =>
        {
            return await dbContext.RelayPreKeys
                .AsNoTracking()
                .AnyAsync(k => k.RoutingToken == routingBytes, ct);
        });
    }

    public Task<IReadOnlyList<SimulatorHostedPreKeyReadModel>> GetHostedOneTimePreKeysAsync(
        BlindedRoutingToken routingToken,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        var routingBytes = routingToken.ToByteArray();
        return WithContextAsync<IReadOnlyList<SimulatorHostedPreKeyReadModel>>(async dbContext =>
        {
            var entity = await dbContext.RelayPreKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.RoutingToken == routingBytes && k.DeviceId == deviceId.Value, ct);

            if (entity == null || string.IsNullOrEmpty(entity.OneTimePreKeysJson))
            {
                return Array.Empty<SimulatorHostedPreKeyReadModel>();
            }

            try
            {
                var dtos = JsonSerializer.Deserialize<List<OpkDto>>(entity.OneTimePreKeysJson);
                if (dtos == null) return Array.Empty<SimulatorHostedPreKeyReadModel>();

                return dtos.Select(d => new SimulatorHostedPreKeyReadModel(d.KeyId, Convert.FromHexString(d.KeyHex))).ToList();
            }
            catch
            {
                return Array.Empty<SimulatorHostedPreKeyReadModel>();
            }
        });
    }

    private sealed class OpkDto
    {
        public uint KeyId { get; set; }
        public string KeyHex { get; set; } = string.Empty;
    }
}
