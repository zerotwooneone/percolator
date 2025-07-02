using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Identity;
using Percolator.Application.Security;
using System.Threading.Tasks;
using System.Threading;
using System;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using Percolator.Network;

namespace Percolator.Application.Identity;

public class IdentityOrchestrator : IIdentityOrchestrator, IHostedService
{
    private readonly IIdentityService _identityService;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ILogger<IdentityOrchestrator> _logger;
    private readonly NodeOptions _options;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ITrustedPeerStore _trustedPeerStore;

    public IdentityOrchestrator(
        IIdentityService identityService,
        IKeyManagementService keyManagementService,
        ILogger<IdentityOrchestrator> logger,
        IOptions<NodeOptions> options,
        ActiveIdentityContext activeIdentityContext,
        IHostApplicationLifetime lifetime,
        ITrustedPeerStore trustedPeerStore)
    {
        _identityService = identityService;
        _keyManagementService = keyManagementService;
        _logger = logger;
        _options = options.Value;
        _activeIdentityContext = activeIdentityContext;
        _lifetime = lifetime;
        _trustedPeerStore = trustedPeerStore;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var identityName = _options.IdentityName;
        if (string.IsNullOrEmpty(identityName))
        {
            _logger.LogError("Identity name is not configured. Please set Node:IdentityName in configuration.");
            _lifetime.StopApplication();
            return;
        }

        await LoadActiveIdentityAsync(identityName, cancellationToken);
    }

    public async Task LoadActiveIdentityAsync(string identityName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identityName))
        {
            _logger.LogInformation("No identity specified. Skipping identity load.");
            return;
        }

        _logger.LogInformation("Loading identity {IdentityName}...", identityName);

        var identity = await _identityService.GetIdentityRecordAsync(identityName, cancellationToken);
        if (identity is null)
        {
            _logger.LogInformation("No identity found with name {IdentityName}. Creating a new one.", identityName);
            identity = await _identityService.CreateIdentityAsync(identityName, _options.IdentityNickname, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Found existing identity {IdentityName}", identityName);
        }

        var keys = await _keyManagementService.GetOrCreateKeysAsync(identityName);

        _activeIdentityContext.Identity = identity;
        _activeIdentityContext.Keys = keys;

        var publicKeyBytes = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        var publicKey = new PublicKey(publicKeyBytes);
        var hash = SHA256.HashData(publicKey.Value);
        var publicKeyHash = new PublicKeyHash(hash);

        //TODO: Update trusted peer store to use public key hash
        //_trustedPeerStore.Add(publicKeyHash);

        _logger.LogInformation("Successfully loaded identity {IdentityName} with public key hash {PublicKeyHash}", identityName, publicKeyHash);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
