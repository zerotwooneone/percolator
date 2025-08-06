using Microsoft.Extensions.Logging;
using Percolator.Application.Configuration;
using Percolator.Identity;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Percolator.Network;

namespace Percolator.Application.Identity;

public class IdentityOrchestrator : IIdentityOrchestrator
{
    private readonly IIdentityService _identityService;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ILogger<IdentityOrchestrator> _logger;
    private readonly NodeOptions _options;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public IdentityOrchestrator(
        IIdentityService identityService,
        IKeyManagementService keyManagementService,
        ILogger<IdentityOrchestrator> logger,
        IOptions<NodeOptions> options,
        ActiveIdentityContext activeIdentityContext)
    {
        _identityService = identityService;
        _keyManagementService = keyManagementService;
        _logger = logger;
        _options = options.Value;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task LoadOrCreateIdentityAsync(string identityName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identityName))
        {
            throw new ArgumentException("Identity name cannot be null or empty.", nameof(identityName));
        }

        var (identity, keys) = await _identityService.GetOrCreateIdentityAsync(identityName, cancellationToken);

        _activeIdentityContext.Identity = identity;
        _activeIdentityContext.Keys = keys;

        var publicKeyHash = new PublicKeyHash(SHA256.HashData(keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()));

        _logger.LogInformation("Successfully loaded identity {IdentityName} with public key hash {PublicKeyHash}", identityName, Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(publicKeyHash.Value)));
    }
}
