using Microsoft.Extensions.Logging;
using Percolator.Application.Configuration;
using Percolator.Identity;
using Microsoft.Extensions.Options;
using Percolator.Identity.Model;

namespace Percolator.Application.Identity;

public class IdentityOrchestrator : IIdentityOrchestrator
{
    private readonly IKeyManagementService _keyManagementService;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ILogger<IdentityOrchestrator> _logger;
    private readonly NodeOptions _options;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public IdentityOrchestrator(
        IKeyManagementService keyManagementService,
        ISelfIdentityRepository selfIdentityRepository,
        ILogger<IdentityOrchestrator> logger,
        IOptions<NodeOptions> options,
        ActiveIdentityContext activeIdentityContext)
    {
        _keyManagementService = keyManagementService;
        _selfIdentityRepository = selfIdentityRepository;
        _logger = logger;
        _options = options.Value;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task ResolveIdentityAsync(string identityName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(identityName))
        {
            throw new ArgumentException("Identity name cannot be null or empty.", nameof(identityName));
        }

        // Resolve and assign SelfIdentityId for scoping
        var dto = await _selfIdentityRepository.GetByNameAsync(identityName);
        if (dto is null)
        {
            throw new InvalidOperationException($"Identity {identityName} not found");
        }
        var keys = await _keyManagementService.GetKeysAsync(dto.Name);
        var identity = new IdentityRecord(dto.PeerId, identityName, dto.Name)  with { SelfIdentityId = dto?.Id ?? 1 };

        _activeIdentityContext.Identity = identity;
        _activeIdentityContext.Keys = keys;

        _logger.LogInformation("Successfully loaded identity {IdentityName} with public key {PublicKey}", identityName, Convert.ToBase64String(keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()));
    }
}
