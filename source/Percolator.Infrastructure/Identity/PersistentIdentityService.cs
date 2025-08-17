using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Infrastructure.Identity;

public class PersistentIdentityService : IIdentityService
{
    private readonly IIdentityStore _identityStore;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ILogger<PersistentIdentityService> _logger;

    public PersistentIdentityService(
        IIdentityStore identityStore,
        IKeyManagementService keyManagementService,
        ILogger<PersistentIdentityService> logger)
    {
        _identityStore = identityStore;
        _keyManagementService = keyManagementService;
        _logger = logger;
    }

    public async Task<(IdentityRecord Identity, X3dhKeys Keys)> GetOrCreateIdentityAsync(string name, CancellationToken cancellationToken = default)
    {
        var identity = await _identityStore.GetIdentityAsync(name, cancellationToken);
        X3dhKeys keys;

        if (identity is null)
        {
            identity = await CreateIdentityAsync(name, name, cancellationToken);
            keys = await _keyManagementService.CreateKeysAsync(name);
        }
        else
        {
            keys = (await _keyManagementService.GetKeysAsync(name) ?? await _keyManagementService.CreateKeysAsync(name));
        }

        return (identity, keys);
    }

    public async Task<IdentityRecord> CreateIdentityAsync(string name, string? nickname, CancellationToken cancellationToken = default)
    {
        var existing = await _identityStore.GetIdentityAsync(name, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Identity '{name}' already exists.");
        }

        var identity = new IdentityRecord(Guid.NewGuid(), name, nickname);
        await _identityStore.StoreIdentityAsync(identity, cancellationToken);
        _logger.LogInformation("Created new identity {IdentityName}", name);
        return identity;
    }

    public async Task<IdentityRecord?> GetIdentityRecordAsync(string name, CancellationToken cancellationToken = default)
    {
        return await _identityStore.GetIdentityAsync(name, cancellationToken);
    }

    public async Task<IEnumerable<string>> ListIdentityNamesAsync(CancellationToken cancellationToken = default)
    {
        return await _identityStore.ListIdentityNamesAsync(cancellationToken);
    }
}
