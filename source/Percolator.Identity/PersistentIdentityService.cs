using Microsoft.Extensions.Logging;
using Percolator.Identity.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Identity;

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

    public async Task<IdentityRecord> CreateIdentityAsync(string name, string? nickname, CancellationToken cancellationToken = default)
    {
        if (await _identityStore.IdentityExistsAsync(name, cancellationToken))
        {
            throw new InvalidOperationException($"An identity with the name '{name}' already exists.");
        }

        // Keys are managed separately, but we can ensure they are created here.
        await _keyManagementService.GetOrCreateKeysAsync(name);

        var identity = new IdentityRecord(Guid.NewGuid(), name, nickname);
        await _identityStore.StoreIdentityAsync(identity, cancellationToken);
        _logger.LogInformation("Created identity {IdentityName} with ID {IdentityId}", name, identity.Id);

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
